// ProximityLock Credential Provider (方案C)
// 手机在附近 (蓝牙判定) => 锁屏界面自动提交凭据登录
//
// 数据来源:
//   C:\ProgramData\ProximityLock\state.ini  -> near=1/0, ts=unix秒 (托盘程序每秒写入)
//   C:\ProgramData\ProximityLock\creds.bin  -> DPAPI(机器范围) 加密的 user\ndomain\npassword
//   C:\ProgramData\ProximityLock\noautologon.flag -> 存在时只显示磁贴, 不自动登录 (调试用)
//   C:\ProgramData\ProximityLock\cp.log     -> 运行日志 (排查"卡在哪一步"用)
//
// v2 修复 (登录界面卡死 / 无限重试):
//   1. SetSelected 不再返回 pbAutoLogon=TRUE
//      —— 原来 GetCredentialCount 和 SetSelected 都返回 TRUE, 等于每次选中都自动提交,
//         凭据一旦不被接受就是"提交->失败->重新枚举->再提交"的死循环, 表现就是界面卡死
//   2. ReportResult 收到失败后熔断 (_autoLogonBlocked), 本次 LogonUI 生命周期内不再自动登录,
//      用户仍可看到磁贴手动提交
//   3. 凭据解密和 LUID 查询只做一次并缓存 (原来每次枚举都跑 DPAPI + LsaEnumerateLogonSessions,
//      LogonUI 会频繁枚举, 累积起来就是明显卡顿)
//   4. 只在 CPUS_UNLOCK_WORKSTATION (锁屏解锁) 场景提供凭据, 开机登录场景完全不介入
//   5. GetFieldState 不再给字段设 CPFIS_FOCUSED (自动登录时抢焦点容易触发异常)
//   6. 字段补上真正的 CPFT_SUBMIT_BUTTON (原来 GetSubmitButtonValue 指向密码框, 描述符不一致)
//   7. 加文件日志, 可直接看到 CP 走到哪一步

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <shlwapi.h>
#include <wtsapi32.h>
#include <sddl.h>
#include <dpapi.h>
#include <cstdio>
#include <cstdarg>
#include <ctime>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "secur32.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "wtsapi32.lib")
#pragma comment(lib, "user32.lib")

// {8D2A5E11-3C7B-4F1E-9A64-5C0E2B7D9F31}
static const GUID CLSID_ProximityProvider =
    {0x8d2a5e11, 0x3c7b, 0x4f1e, {0x9a, 0x64, 0x5c, 0x0e, 0x2b, 0x7d, 0x9f, 0x31}};

static const wchar_t* kDataDir = L"C:\\ProgramData\\ProximityLock";
static const wchar_t* kStateFile = L"C:\\ProgramData\\ProximityLock\\state.ini";
static const wchar_t* kCredsFile = L"C:\\ProgramData\\ProximityLock\\creds.bin";
static const wchar_t* kLogFile = L"C:\\ProgramData\\ProximityLock\\cp.log";
static const wchar_t* kNoAutoLogonFlag = L"C:\\ProgramData\\ProximityLock\\noautologon.flag";
static const DWORD kStateFreshSeconds = 6;   // 状态超过这个秒数视为失效

enum
{
    FIELD_USER = 0,
    FIELD_PASSWORD = 1,
    FIELD_SUBMIT = 2,
    FIELD_COUNT = 3
};

// ---------------------------------------------------------------- 日志

static void Log(const wchar_t* fmt, ...)
{
    WIN32_FILE_ATTRIBUTE_DATA fad{};
    if (GetFileAttributesExW(kLogFile, GetFileExInfoStandard, &fad) &&
        fad.nFileSizeHigh == 0 && fad.nFileSizeLow > 2 * 1024 * 1024)
        return;   // 超过 2MB 停止写入

    wchar_t msg[1024];
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf_s(msg, _countof(msg), _TRUNCATE, fmt, ap);
    va_end(ap);

    SYSTEMTIME st{};
    GetLocalTime(&st);
    wchar_t line[1200];
    _snwprintf_s(line, _countof(line), _TRUNCATE,
                 L"[%02d:%02d:%02d.%03d][tid %5lu] %s\r\n",
                 st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                 GetCurrentThreadId(), msg);

    int len = WideCharToMultiByte(CP_UTF8, 0, line, -1, nullptr, 0, nullptr, nullptr);
    if (len <= 1) return;
    std::vector<char> utf8(static_cast<size_t>(len));
    WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8.data(), len, nullptr, nullptr);

    HANDLE h = CreateFileW(kLogFile, FILE_APPEND_DATA,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written = 0;
    WriteFile(h, utf8.data(), static_cast<DWORD>(len - 1), &written, nullptr);
    CloseHandle(h);
}

// ---------------------------------------------------------------- 工具函数

static bool FileExists(const wchar_t* path)
{
    return GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
}

static bool ReadFileBytes(const wchar_t* path, std::vector<BYTE>& out)
{
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart <= 0 || size.QuadPart > 65536)
    {
        CloseHandle(h);
        return false;
    }
    out.resize(static_cast<size_t>(size.QuadPart));
    DWORD read = 0;
    BOOL ok = ReadFile(h, out.data(), static_cast<DWORD>(out.size()), &read, nullptr);
    CloseHandle(h);
    return ok && read == out.size();
}

static std::wstring ReadIniValue(const wchar_t* path, const wchar_t* key)
{
    std::vector<BYTE> buf;
    if (!ReadFileBytes(path, buf)) return L"";
    int len = MultiByteToWideChar(CP_UTF8, 0, reinterpret_cast<LPCCH>(buf.data()),
                                 static_cast<int>(buf.size()), nullptr, 0);
    std::wstring text(len, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, reinterpret_cast<LPCCH>(buf.data()),
                        static_cast<int>(buf.size()), text.data(), len);

    std::wstring keyEq = std::wstring(key) + L"=";
    size_t pos = 0;
    while (pos < text.size())
    {
        size_t eol = text.find(L'\n', pos);
        if (eol == std::wstring::npos) eol = text.size();
        std::wstring line = text.substr(pos, eol - pos);
        if (!line.empty() && line.back() == L'\r') line.pop_back();
        if (line.rfind(keyEq, 0) == 0) return line.substr(keyEq.size());
        pos = eol + 1;
    }
    return L"";
}

/// 状态文件是否新鲜且 near=1
static bool IsProximityNear()
{
    std::wstring nearFlag = ReadIniValue(kStateFile, L"near");
    std::wstring ts = ReadIniValue(kStateFile, L"ts");
    if (nearFlag != L"1" || ts.empty()) return false;
    long long stamp = _wtoi64(ts.c_str());
    long long now = static_cast<long long>(time(nullptr));
    if (stamp <= 0) return false;
    long long age = now - stamp;
    if (age < -120) return false;             // 时钟明显漂移, 不信任
    return age <= static_cast<long long>(kStateFreshSeconds);
}

struct Account
{
    std::wstring user;
    std::wstring domain;
    std::wstring password;
    bool valid = false;
};

/// 读 creds.bin: 前 4 字节长度 + DPAPI 密文, 明文格式 user\n domain\n password
static Account LoadCredential()
{
    Account acc;
    std::vector<BYTE> raw;
    if (!ReadFileBytes(kCredsFile, raw) || raw.size() < 4) return acc;

    DWORD blobLen = 0;
    memcpy(&blobLen, raw.data(), sizeof(DWORD));
    if (blobLen == 0 || raw.size() < 4 + blobLen) return acc;

    DATA_BLOB in{blobLen, raw.data() + 4};
    DATA_BLOB out{};
    if (!CryptUnprotectData(&in, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_LOCAL_MACHINE, &out))
    {
        Log(L"LoadCredential: CryptUnprotectData failed, err=%lu", GetLastError());
        return acc;
    }

    std::string utf8(reinterpret_cast<char*>(out.pbData), out.cbData);
    LocalFree(out.pbData);

    size_t p1 = utf8.find('\n');
    size_t p2 = (p1 == std::string::npos) ? std::string::npos : utf8.find('\n', p1 + 1);
    if (p1 == std::string::npos || p2 == std::string::npos)
    {
        Log(L"LoadCredential: plaintext format invalid");
        return acc;
    }

    auto toW = [](const std::string& s) {
        int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
        std::wstring w(len > 0 ? len - 1 : 0, L'\0');
        if (len > 0) MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), len);
        return w;
    };

    acc.user = toW(utf8.substr(0, p1));
    acc.domain = toW(utf8.substr(p1 + 1, p2 - p1 - 1));
    acc.password = toW(utf8.substr(p2 + 1));
    acc.valid = !acc.user.empty() && !acc.password.empty();
    SecureZeroMemory(&utf8[0], utf8.size());
    return acc;
}

/// 找要解锁的那个登录会话的 LUID
/// 三级匹配, 从最精确到最宽松 —— LogonUI 里会话编号的来源有几个, 不能只信 WTSGetActiveConsoleSessionId
///   1) 本进程所在会话 + 用户名一致   (LogonUI 就运行在要解锁的用户会话里, 最靠谱)
///   2) 控制台会话 + 用户名一致
///   3) 任意交互会话 + 用户名一致
static bool GetInteractiveLuid(const std::wstring& user, LUID& luid)
{
    ULONG count = 0;
    PLUID sessions = nullptr;
    if (LsaEnumerateLogonSessions(&count, &sessions) != 0 || !sessions)
    {
        Log(L"GetInteractiveLuid: LsaEnumerateLogonSessions failed, err=%lu", GetLastError());
        return false;
    }

    DWORD mySession = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &mySession)) mySession = 0xFFFFFFFF;
    DWORD consoleSession = WTSGetActiveConsoleSessionId();
    Log(L"GetInteractiveLuid: user=%s mySession=%lu consoleSession=%lu",
        user.c_str(), mySession, consoleSession);

    LUID sameSession{}; bool sameFound = false;
    LUID consoleMatch{}; bool consoleFound = false;
    LUID anyMatch{}; bool anyFound = false;

    for (ULONG i = 0; i < count; ++i)
    {
        PSECURITY_LOGON_SESSION_DATA data = nullptr;
        if (LsaGetLogonSessionData(&sessions[i], &data) != 0 || !data) continue;

        if (data->LogonType == Interactive && data->UserName.Buffer && data->UserName.Length > 0)
        {
            std::wstring name(data->UserName.Buffer, data->UserName.Length / sizeof(wchar_t));
            if (_wcsicmp(name.c_str(), user.c_str()) == 0)
            {
                Log(L"GetInteractiveLuid: candidate session=%lu luid=%lu:%ld",
                    data->Session, data->LogonId.HighPart, data->LogonId.LowPart);
                if (data->Session == mySession && !sameFound) { sameSession = data->LogonId; sameFound = true; }
                if (data->Session == consoleSession && !consoleFound) { consoleMatch = data->LogonId; consoleFound = true; }
                if (!anyFound) { anyMatch = data->LogonId; anyFound = true; }
            }
        }
        LsaFreeReturnBuffer(data);
    }
    LsaFreeReturnBuffer(sessions);

    if (sameFound) { luid = sameSession; return true; }
    if (consoleFound) { luid = consoleMatch; return true; }
    if (anyFound) { luid = anyMatch; return true; }

    Log(L"GetInteractiveLuid: no matching interactive session for %s", user.c_str());
    return false;
}

/// 目标账户的 SID 字符串
static HRESULT GetUserSidString(const Account& acc, wchar_t** sidOut)
{
    *sidOut = nullptr;
    BYTE sidBuf[SECURITY_MAX_SID_SIZE]{};
    DWORD sidSize = sizeof(sidBuf);
    wchar_t domain[256]{};
    DWORD domainSize = 256;
    SID_NAME_USE use;
    if (!LookupAccountNameW(acc.domain.empty() ? nullptr : acc.domain.c_str(), acc.user.c_str(),
                            sidBuf, &sidSize, domain, &domainSize, &use))
    {
        Log(L"LookupAccountName(%s\\%s) failed, err=%lu", acc.domain.c_str(), acc.user.c_str(), GetLastError());
        return HRESULT_FROM_WIN32(GetLastError());
    }

    LPWSTR str = nullptr;
    if (!ConvertSidToStringSidW(static_cast<PSID>(sidBuf), &str)) return E_FAIL;
    *sidOut = static_cast<wchar_t*>(CoTaskMemAlloc((wcslen(str) + 1) * sizeof(wchar_t)));
    if (!*sidOut)
    {
        LocalFree(str);
        return E_OUTOFMEMORY;
    }
    wcscpy_s(*sidOut, wcslen(str) + 1, str);
    LocalFree(str);
    return S_OK;
}

static HRESULT GetNegotiatePackage(DWORD* package)
{
    HANDLE lsa = nullptr;
    if (LsaConnectUntrusted(&lsa) != 0) return E_FAIL;
    LSA_STRING name;
    name.Buffer = const_cast<char*>("Negotiate");
    name.Length = static_cast<USHORT>(strlen(name.Buffer));
    name.MaximumLength = name.Length + 1;
    ULONG id = 0;
    NTSTATUS st = LsaLookupAuthenticationPackage(lsa, &name, &id);
    LsaClose(lsa);
    if (st != 0) return E_FAIL;
    *package = id;
    return S_OK;
}

/// 打包 KERB_INTERACTIVE_(UNLOCK_)LOGON
static HRESULT PackLogonBuffer(const Account& acc, bool unlock, LUID luid,
                              std::vector<BYTE>& out, DWORD* authPackage)
{
    DWORD package = 0;
    if (FAILED(GetNegotiatePackage(&package))) return E_FAIL;
    *authPackage = package;

    size_t headerSize = unlock ? sizeof(KERB_INTERACTIVE_UNLOCK_LOGON)
                               : sizeof(KERB_INTERACTIVE_LOGON);
    size_t userLen = acc.user.size() * sizeof(wchar_t);
    size_t domainLen = acc.domain.size() * sizeof(wchar_t);
    size_t passLen = acc.password.size() * sizeof(wchar_t);

    out.assign(headerSize + userLen + domainLen + passLen, 0);

    UNICODE_STRING uUser{}, uDomain{}, uPass{};
    uUser.Buffer = reinterpret_cast<PWSTR>(out.data() + headerSize);
    uUser.Length = static_cast<USHORT>(userLen);
    uUser.MaximumLength = static_cast<USHORT>(userLen);
    if (userLen) memcpy(uUser.Buffer, acc.user.data(), userLen);

    uDomain.Buffer = reinterpret_cast<PWSTR>(out.data() + headerSize + userLen);
    uDomain.Length = static_cast<USHORT>(domainLen);
    uDomain.MaximumLength = static_cast<USHORT>(domainLen);
    if (domainLen) memcpy(uDomain.Buffer, acc.domain.data(), domainLen);

    uPass.Buffer = reinterpret_cast<PWSTR>(out.data() + headerSize + userLen + domainLen);
    uPass.Length = static_cast<USHORT>(passLen);
    uPass.MaximumLength = static_cast<USHORT>(passLen);
    if (passLen) memcpy(uPass.Buffer, acc.password.data(), passLen);

    if (unlock)
    {
        auto* k = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(out.data());
        k->LogonId = luid;
        k->Logon.MessageType = KerbInteractiveLogon;
        k->Logon.UserName = uUser;
        k->Logon.LogonDomainName = uDomain;
        k->Logon.Password = uPass;
    }
    else
    {
        auto* k = reinterpret_cast<KERB_INTERACTIVE_LOGON*>(out.data());
        k->MessageType = KerbInteractiveLogon;
        k->UserName = uUser;
        k->LogonDomainName = uDomain;
        k->Password = uPass;
    }
    return S_OK;
}

// ---------------------------------------------------------------- 凭据对象

class CProvider;

class CCredential : public ICredentialProviderCredential2
{
public:
    CCredential(const Account& acc, DWORD scenario, CProvider* owner, LUID luid)
        : _ref(1), _acc(acc), _scenario(scenario), _owner(owner), _luid(luid)
    {
    }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_INVALIDARG;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProviderCredential ||
            riid == IID_ICredentialProviderCredential2)
            *ppv = static_cast<ICredentialProviderCredential2*>(this);
        if (!*ppv) return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG c = InterlockedDecrement(&_ref);
        if (c == 0) delete this;
        return c;
    }

    // ICredentialProviderCredential
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents*) override { return S_OK; }
    IFACEMETHODIMP UnAdvise() override { return S_OK; }

    /// 重要: 这里必须返回 FALSE
    /// 返回 TRUE 表示"只要这个磁贴被选中就自动提交", 配合 GetCredentialCount 的 pbAutoLogon
    /// 会形成双重触发; 凭据一旦不被接受, LogonUI 就陷入"提交->失败->重新枚举->再提交"的死循环.
    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogon) override
    {
        if (pbAutoLogon) *pbAutoLogon = FALSE;
        Log(L"SetSelected -> FALSE");
        return S_OK;
    }
    IFACEMETHODIMP SetDeselected() override { return S_OK; }

    IFACEMETHODIMP GetFieldState(DWORD dwFieldID, CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
                                 CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override
    {
        if (dwFieldID >= FIELD_COUNT || !pcpfs || !pcpfis) return E_INVALIDARG;
        *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
        *pcpfis = CPFIS_NONE;   // 自动登录时不要抢焦点
        return S_OK;
    }
    IFACEMETHODIMP GetStringValue(DWORD dwFieldID, wchar_t** ppwsz) override
    {
        if (!ppwsz) return E_INVALIDARG;
        const std::wstring* v = nullptr;
        if (dwFieldID == FIELD_USER) v = &_acc.user;
        else if (dwFieldID == FIELD_PASSWORD) v = &_acc.password;
        else return E_INVALIDARG;

        *ppwsz = static_cast<wchar_t*>(CoTaskMemAlloc((v->size() + 1) * sizeof(wchar_t)));
        if (!*ppwsz) return E_OUTOFMEMORY;
        wcscpy_s(*ppwsz, v->size() + 1, v->c_str());
        return S_OK;
    }
    IFACEMETHODIMP GetBitmapValue(DWORD, HBITMAP* phbmp) override
    {
        if (phbmp) *phbmp = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetCheckboxValue(DWORD, BOOL* pbChecked, wchar_t** ppwszLabel) override
    {
        if (pbChecked) *pbChecked = FALSE;
        if (ppwszLabel) *ppwszLabel = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetComboBoxValueCount(DWORD, DWORD* pcItems, DWORD* pdwSelectedItem) override
    {
        if (pcItems) *pcItems = 0;
        if (pdwSelectedItem) *pdwSelectedItem = 0;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetComboBoxValueAt(DWORD, DWORD, wchar_t** ppwszItem) override
    {
        if (ppwszItem) *ppwszItem = nullptr;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP GetSubmitButtonValue(DWORD dwFieldID, DWORD* pdwAdjacentTo) override
    {
        if (!pdwAdjacentTo || dwFieldID != FIELD_SUBMIT) return E_INVALIDARG;
        *pdwAdjacentTo = FIELD_PASSWORD;   // 按钮在密码框后面
        return S_OK;
    }

    /// 允许用户在磁贴里手动改账户/密码 (自动登录失败时的兜底)
    IFACEMETHODIMP SetStringValue(DWORD dwFieldID, PCWSTR pwz) override
    {
        if (!pwz) return E_INVALIDARG;
        if (dwFieldID == FIELD_USER) { _acc.user = pwz; return S_OK; }
        if (dwFieldID == FIELD_PASSWORD) { _acc.password = pwz; return S_OK; }
        return E_INVALIDARG;
    }
    IFACEMETHODIMP SetCheckboxValue(DWORD, BOOL) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD, DWORD) override { return E_NOTIMPL; }
    IFACEMETHODIMP CommandLinkClicked(DWORD) override { return E_NOTIMPL; }

    /// 系统汇报提交结果: 失败就熔断自动登录, 避免无限重试把界面拖死
    IFACEMETHODIMP ReportResult(NTSTATUS ntsStatus, NTSTATUS ntsSubstatus,
                               wchar_t** ppwszOptionalStatusText,
                               CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override;

    /// 核心: 交给系统的凭据序列化包
    IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
                                    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
                                    wchar_t** ppwszOptionalStatusText,
                                    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override
    {
        if (!pcpgsr || !pcpcs) return E_INVALIDARG;
        if (ppwszOptionalStatusText) *ppwszOptionalStatusText = nullptr;
        if (pcpsiOptionalStatusIcon) *pcpsiOptionalStatusIcon = CPSI_NONE;
        ZeroMemory(pcpcs, sizeof(*pcpcs));
        *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;

        if (_acc.user.empty() || _acc.password.empty())
        {
            Log(L"GetSerialization: empty user/password, abort");
            *pcpgsr = CPGSR_NO_CREDENTIAL_FINISHED;
            return S_OK;
        }

        bool unlock = (_scenario == CPUS_UNLOCK_WORKSTATION);
        std::vector<BYTE> buf;
        DWORD package = 0;
        if (FAILED(PackLogonBuffer(_acc, unlock, _luid, buf, &package)))
        {
            Log(L"GetSerialization: PackLogonBuffer failed");
            *pcpgsr = CPGSR_NO_CREDENTIAL_FINISHED;
            return S_OK;
        }

        pcpcs->rgbSerialization = static_cast<BYTE*>(CoTaskMemAlloc(buf.size()));
        if (!pcpcs->rgbSerialization)
        {
            *pcpgsr = CPGSR_NO_CREDENTIAL_FINISHED;
            return E_OUTOFMEMORY;
        }
        memcpy(pcpcs->rgbSerialization, buf.data(), buf.size());
        pcpcs->cbSerialization = static_cast<ULONG>(buf.size());
        pcpcs->ulAuthenticationPackage = package;
        pcpcs->clsidCredentialProvider = CLSID_ProximityProvider;

        Log(L"GetSerialization: OK unlock=%d user=%s pkg=%lu bytes=%zu",
            unlock ? 1 : 0, _acc.user.c_str(), package, buf.size());
        *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;
        return S_OK;
    }

    // ICredentialProviderCredential2
    /// 查不到 SID 不算错误: 返回 S_OK + nullptr, 让 LogonUI 当普通凭据处理
    IFACEMETHODIMP GetUserSid(wchar_t** ppszSid) override
    {
        if (!ppszSid) return E_INVALIDARG;
        if (FAILED(GetUserSidString(_acc, ppszSid)))
        {
            *ppszSid = nullptr;
            return S_OK;
        }
        return S_OK;
    }

private:
    ~CCredential() = default;
    LONG _ref;
    Account _acc;
    DWORD _scenario;
    CProvider* _owner;
    LUID _luid;
};

// ---------------------------------------------------------------- 提供程序

class CProvider : public ICredentialProvider
{
public:
    CProvider() : _ref(1), _scenario(CPUS_INVALID), _credential(nullptr),
                  _events(nullptr), _adviseContext(0), _thread(nullptr),
                  _stop(false), _wasNear(false), _autoLogonBlocked(false),
                  _cacheTried(false), _cacheValid(false), _luidOk(false)
    {
        _luid.LowPart = 0;
        _luid.HighPart = 0;
        Log(L"--- CProvider created (pid %lu) ---", GetCurrentProcessId());
    }
    ~CProvider()
    {
        StopWatcher();
        if (_credential) _credential->Release();
        if (_events) _events->Release();
        Log(L"--- CProvider destroyed ---");
    }

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_INVALIDARG;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_ICredentialProvider)
            *ppv = static_cast<ICredentialProvider*>(this);
        if (!*ppv) return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG c = InterlockedDecrement(&_ref);
        if (c == 0) delete this;
        return c;
    }

    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD) override
    {
        _scenario = cpus;
        Log(L"SetUsageScenario: %lu", static_cast<ULONG>(cpus));
        if (cpus == CPUS_LOGON || cpus == CPUS_UNLOCK_WORKSTATION) return S_OK;
        return E_NOTIMPL;
    }
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override
    {
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Advise(ICredentialProviderEvents* events, UINT_PTR context) override
    {
        if (_events) { _events->Release(); _events = nullptr; }
        _events = events;
        if (_events) _events->AddRef();
        _adviseContext = context;
        Log(L"Advise: context=0x%llx", static_cast<unsigned long long>(context));
        StartWatcher();
        return S_OK;
    }

    IFACEMETHODIMP UnAdvise() override
    {
        Log(L"UnAdvise");
        StopWatcher();
        if (_events) { _events->Release(); _events = nullptr; }
        return S_OK;
    }

    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* pdwCount) override
    {
        if (!pdwCount) return E_INVALIDARG;
        *pdwCount = FIELD_COUNT;
        return S_OK;
    }
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd) override
    {
        if (!ppcpfd || dwIndex >= FIELD_COUNT) return E_INVALIDARG;
        *ppcpfd = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(
            CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
        if (!*ppcpfd) return E_OUTOFMEMORY;
        ZeroMemory(*ppcpfd, sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR));
        (*ppcpfd)->dwFieldID = dwIndex;

        const wchar_t* label = L"";
        switch (dwIndex)
        {
        case FIELD_USER:     (*ppcpfd)->cpft = CPFT_EDIT_TEXT;     label = L"账户"; break;
        case FIELD_PASSWORD: (*ppcpfd)->cpft = CPFT_PASSWORD_TEXT; label = L"密码"; break;
        default:             (*ppcpfd)->cpft = CPFT_SUBMIT_BUTTON; label = L"提交"; break;
        }
        (*ppcpfd)->pszLabel = static_cast<wchar_t*>(CoTaskMemAlloc((wcslen(label) + 1) * sizeof(wchar_t)));
        if ((*ppcpfd)->pszLabel) wcscpy_s((*ppcpfd)->pszLabel, wcslen(label) + 1, label);
        (*ppcpfd)->guidFieldType = GUID_NULL;
        return S_OK;
    }

    /// 手机在附近 => 1 个凭据 + 默认选它 + pbAutoLogon=TRUE => LogonUI 自动登录
    IFACEMETHODIMP GetCredentialCount(DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogon) override
    {
        if (!pdwCount || !pdwDefault || !pbAutoLogon) return E_INVALIDARG;
        *pdwCount = 0;
        *pdwDefault = CREDENTIAL_PROVIDER_NO_DEFAULT;
        *pbAutoLogon = FALSE;

        // 只在锁屏解锁场景介入; 开机登录不碰, 避免进不去系统
        if (_scenario != CPUS_UNLOCK_WORKSTATION) return S_OK;

        if (!IsProximityNear())
        {
            Log(L"GetCredentialCount: near=0 -> 0 credentials");
            return S_OK;
        }

        EnsureCache();
        if (!_cacheValid)
        {
            Log(L"GetCredentialCount: near=1 but cache invalid (creds or LUID unavailable)");
            return S_OK;
        }

        if (!_credential)
        {
            _credential = new (std::nothrow) CCredential(_cacheAcc, _scenario, this, _luid);
            if (!_credential) return E_OUTOFMEMORY;
        }

        bool noAuto = FileExists(kNoAutoLogonFlag);
        *pdwCount = 1;
        *pdwDefault = 0;
        *pbAutoLogon = (noAuto || _autoLogonBlocked) ? FALSE : TRUE;
        Log(L"GetCredentialCount: near=1 -> 1 credential, autologon=%d (flag=%d blocked=%d)",
            *pbAutoLogon ? 1 : 0, noAuto ? 1 : 0, _autoLogonBlocked ? 1 : 0);
        return S_OK;
    }

    IFACEMETHODIMP GetCredentialAt(DWORD dwIndex, ICredentialProviderCredential** ppcpc) override
    {
        if (!ppcpc) return E_INVALIDARG;
        *ppcpc = nullptr;
        if (dwIndex != 0 || !_credential) return E_INVALIDARG;
        *ppcpc = _credential;
        _credential->AddRef();
        return S_OK;
    }

    /// 由 CCredential::ReportResult 调用: 登录失败后禁止再次自动登录
    void BlockAutoLogon()
    {
        if (!_autoLogonBlocked)
        {
            _autoLogonBlocked = true;
            Log(L"*** auto-logon BLOCKED after failure (手动点提交仍可用) ***");
        }
    }

private:
    /// 凭据解密 + LUID 查询只做一次 (LogonUI 会频繁枚举, 每次都做会明显卡)
    void EnsureCache()
    {
        if (_cacheTried) return;
        _cacheTried = true;

        _cacheAcc = LoadCredential();
        if (!_cacheAcc.valid)
        {
            Log(L"EnsureCache: credential invalid");
            return;
        }
        _luidOk = GetInteractiveLuid(_cacheAcc.user, _luid);
        Log(L"EnsureCache: user=%s domain=%s luidOk=%d luid=%lu:%ld",
            _cacheAcc.user.c_str(), _cacheAcc.domain.c_str(),
            _luidOk ? 1 : 0, _luid.HighPart, _luid.LowPart);
        _cacheValid = _luidOk;
    }

    void StartWatcher()
    {
        if (_thread) return;
        _stop = false;
        _thread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
        Log(L"watcher started: %p", _thread);
    }

    void StopWatcher()
    {
        if (!_thread) return;
        _stop = true;
        WaitForSingleObject(_thread, 3000);
        CloseHandle(_thread);
        _thread = nullptr;
        Log(L"watcher stopped");
    }

    static DWORD WINAPI ThreadProc(LPVOID param)
    {
        auto* self = static_cast<CProvider*>(param);
        // 自己持一个引用, 防止回调期间对象被释放
        self->AddRef();
        while (!self->_stop)
        {
            Sleep(800);
            if (self->_stop) break;
            bool nowNear = IsProximityNear();
            if (nowNear && !self->_wasNear && self->_events)
            {
                Log(L"watcher: near became 1 -> CredentialsChanged");
                self->_events->CredentialsChanged(self->_adviseContext);
                Log(L"watcher: CredentialsChanged returned");
            }
            self->_wasNear = nowNear;
        }
        self->Release();
        return 0;
    }

    LONG _ref;
    DWORD _scenario;
    CCredential* _credential;
    ICredentialProviderEvents* _events;
    UINT_PTR _adviseContext;
    HANDLE _thread;
    volatile bool _stop;
    bool _wasNear;
    volatile bool _autoLogonBlocked;

    bool _cacheTried;
    bool _cacheValid;
    bool _luidOk;
    LUID _luid;
    Account _cacheAcc;
};

IFACEMETHODIMP CCredential::ReportResult(NTSTATUS ntsStatus, NTSTATUS ntsSubstatus,
                                        wchar_t** ppwszOptionalStatusText,
                                        CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon)
{
    Log(L"ReportResult: status=0x%08lX sub=0x%08lX",
        static_cast<unsigned long>(ntsStatus), static_cast<unsigned long>(ntsSubstatus));
    if (ppwszOptionalStatusText) *ppwszOptionalStatusText = nullptr;
    if (pcpsiOptionalStatusIcon) *pcpsiOptionalStatusIcon = CPSI_NONE;

    if (ntsStatus != 0 && _owner)
    {
        _owner->BlockAutoLogon();   // 失败一次就不再自动重试, 避免界面被拖死
    }
    return S_OK;
}

// ---------------------------------------------------------------- 类工厂

class CClassFactory : public IClassFactory
{
public:
    CClassFactory() : _ref(1) {}

    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_INVALIDARG;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
            *ppv = static_cast<IClassFactory*>(this);
        if (!*ppv) return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG c = InterlockedDecrement(&_ref);
        if (c == 0) delete this;
        return c;
    }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* provider = new (std::nothrow) CProvider();
        if (!provider) return E_OUTOFMEMORY;
        HRESULT hr = provider->QueryInterface(riid, ppv);
        provider->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&_serverLocks);
        else InterlockedDecrement(&_serverLocks);
        return S_OK;
    }

private:
    ~CClassFactory() = default;
    LONG _ref;
    static LONG _serverLocks;
};

LONG CClassFactory::_serverLocks = 0;

// ---------------------------------------------------------------- DLL 导出

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_INVALIDARG;
    if (!IsEqualCLSID(rclsid, CLSID_ProximityProvider)) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) CClassFactory();
    if (!factory) return E_OUTOFMEMORY;
    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return S_FALSE;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        Log(L"=== DLL_PROCESS_ATTACH pid=%lu ===", GetCurrentProcessId());
    }
    return TRUE;
}
