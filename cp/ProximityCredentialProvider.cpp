// ProximityLock Credential Provider (方案C)
// 手机在附近 (蓝牙判定) => 锁屏界面自动提交凭据登录
//
// 关键机制: ICredentialProvider::GetCredentialCount 的 pbAutoLogon=TRUE 让 LogonUI 自动登录
// 数据来源:
//   C:\ProgramData\ProximityLock\state.ini  -> near=1/0, ts=unix秒 (由托盘程序每秒写入)
//   C:\ProgramData\ProximityLock\creds.bin  -> DPAPI(机器范围) 加密的 user|domain|password

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <shlwapi.h>
#include <wtsapi32.h>
#include <sddl.h>
#include <dpapi.h>
#include <cstdio>
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
static const DWORD kStateFreshSeconds = 6;   // 状态超过这个秒数视为失效 (托盘程序挂了就不解锁)

// ---------------------------------------------------------------- 工具函数

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
    // UTF-8 -> UTF-16
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

/// 状态文件是否新鲜且 near=1 (托盘程序每秒刷新; 托盘挂了/手机离开都返回 false)
static bool IsProximityNear()
{
    std::wstring nearFlag = ReadIniValue(kStateFile, L"near");
    std::wstring ts = ReadIniValue(kStateFile, L"ts");
    if (nearFlag != L"1" || ts.empty()) return false;
    long long stamp = _wtoi64(ts.c_str());
    long long now = static_cast<long long>(time(nullptr));
    if (stamp <= 0) return false;
    return (now - stamp) <= static_cast<long long>(kStateFreshSeconds);
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
        return acc;

    std::string utf8(reinterpret_cast<char*>(out.pbData), out.cbData);
    LocalFree(out.pbData);

    size_t p1 = utf8.find('\n');
    size_t p2 = (p1 == std::string::npos) ? std::string::npos : utf8.find('\n', p1 + 1);
    if (p1 == std::string::npos || p2 == std::string::npos) return acc;

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
    return acc;
}

/// 找当前交互登录会话的 LUID (解锁必须带)
static bool GetInteractiveLuid(const std::wstring& user, LUID& luid)
{
    ULONG count = 0;
    PLUID sessions = nullptr;
    if (LsaEnumerateLogonSessions(&count, &sessions) != 0 || !sessions) return false;

    bool found = false;
    DWORD consoleSession = WTSGetActiveConsoleSessionId();
    for (ULONG i = 0; i < count && !found; ++i)
    {
        PSECURITY_LOGON_SESSION_DATA data = nullptr;
        if (LsaGetLogonSessionData(&sessions[i], &data) == 0 && data)
        {
            std::wstring name(data->UserName.Buffer, data->UserName.Length / sizeof(wchar_t));
            if (data->LogonType == Interactive && data->Session == consoleSession &&
                _wcsicmp(name.c_str(), user.c_str()) == 0)
            {
                luid = data->LogonId;
                found = true;
            }
            LsaFreeReturnBuffer(data);
        }
    }
    LsaFreeReturnBuffer(sessions);
    return found;
}

/// 目标账户的 SID 字符串 (CredentialProviderCredential2 需要)
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
        return HRESULT_FROM_WIN32(GetLastError());

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
    memcpy(uUser.Buffer, acc.user.data(), userLen);

    uDomain.Buffer = reinterpret_cast<PWSTR>(out.data() + headerSize + userLen);
    uDomain.Length = static_cast<USHORT>(domainLen);
    uDomain.MaximumLength = static_cast<USHORT>(domainLen);
    if (domainLen) memcpy(uDomain.Buffer, acc.domain.data(), domainLen);

    uPass.Buffer = reinterpret_cast<PWSTR>(out.data() + headerSize + userLen + domainLen);
    uPass.Length = static_cast<USHORT>(passLen);
    uPass.MaximumLength = static_cast<USHORT>(passLen);
    memcpy(uPass.Buffer, acc.password.data(), passLen);

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
    CCredential(const Account& acc, DWORD scenario) : _ref(1), _acc(acc), _scenario(scenario) {}

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
    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogon) override
    {
        if (pbAutoLogon) *pbAutoLogon = TRUE;   // 选中即自动提交
        return S_OK;
    }
    IFACEMETHODIMP SetDeselected() override { return S_OK; }

    IFACEMETHODIMP GetFieldState(DWORD dwFieldID, CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
                                 CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override
    {
        if (dwFieldID > 1 || !pcpfs || !pcpfis) return E_INVALIDARG;
        *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
        *pcpfis = dwFieldID == 1 ? CPFIS_FOCUSED : CPFIS_NONE;
        return S_OK;
    }
    IFACEMETHODIMP GetStringValue(DWORD dwFieldID, wchar_t** ppwsz) override
    {
        if (!ppwsz) return E_INVALIDARG;
        const std::wstring* v = nullptr;
        if (dwFieldID == 0) v = &_acc.user;
        else if (dwFieldID == 1) v = &_acc.password;
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
        if (pdwAdjacentTo) *pdwAdjacentTo = 1;
        return dwFieldID == 1 ? S_OK : E_INVALIDARG;
    }
    IFACEMETHODIMP SetStringValue(DWORD, PCWSTR) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetCheckboxValue(DWORD, BOOL) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD, DWORD) override { return E_NOTIMPL; }
    IFACEMETHODIMP CommandLinkClicked(DWORD) override { return E_NOTIMPL; }
    IFACEMETHODIMP ReportResult(NTSTATUS, NTSTATUS, wchar_t** ppwszOptionalStatusText,
                                CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override
    {
        if (ppwszOptionalStatusText) *ppwszOptionalStatusText = nullptr;
        if (pcpsiOptionalStatusIcon) *pcpsiOptionalStatusIcon = CPSI_NONE;
        return S_OK;
    }

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

        bool unlock = (_scenario == CPUS_UNLOCK_WORKSTATION);
        LUID luid{};
        if (unlock && !GetInteractiveLuid(_acc.user, luid))
        {
            // 找不到会话 LUID 说明凭据/账户不匹配, 交回系统让用户输密码
            *pcpgsr = CPGSR_NO_CREDENTIAL_FINISHED;
            return S_OK;
        }

        std::vector<BYTE> buf;
        DWORD package = 0;
        if (FAILED(PackLogonBuffer(_acc, unlock, luid, buf, &package)))
        {
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

        *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;
        return S_OK;
    }

    // ICredentialProviderCredential2
    IFACEMETHODIMP GetUserSid(wchar_t** ppszSid) override
    {
        if (!ppszSid) return E_INVALIDARG;
        return GetUserSidString(_acc, ppszSid);
    }

private:
    ~CCredential() = default;
    LONG _ref;
    Account _acc;
    DWORD _scenario;
};

// ---------------------------------------------------------------- 提供程序

class CProvider : public ICredentialProvider
{
public:
    CProvider() : _ref(1), _scenario(CPUS_INVALID), _credential(nullptr),
                  _events(nullptr), _adviseContext(0), _thread(nullptr),
                  _stop(false), _wasNear(false) {}
    ~CProvider()
    {
        StopWatcher();
        if (_credential) _credential->Release();
        if (_events) _events->Release();
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
        if (cpus == CPUS_LOGON || cpus == CPUS_UNLOCK_WORKSTATION) return S_OK;
        return E_NOTIMPL;   // 只在这两种场景出现
    }
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) override
    {
        return E_NOTIMPL;
    }

    /// 关键: 锁屏时 CP 已加载 (那时手机不在), 靠后台线程轮询状态文件,
    /// 手机回到附近时主动通知 LogonUI 刷新 -> 重新询问凭据数 -> 自动登录
    IFACEMETHODIMP Advise(ICredentialProviderEvents* events, UINT_PTR context) override
    {
        if (_events) { _events->Release(); _events = nullptr; }
        _events = events;
        if (_events) _events->AddRef();
        _adviseContext = context;
        StartWatcher();
        return S_OK;
    }

    IFACEMETHODIMP UnAdvise() override
    {
        StopWatcher();
        if (_events) { _events->Release(); _events = nullptr; }
        return S_OK;
    }

    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* pdwCount) override
    {
        if (!pdwCount) return E_INVALIDARG;
        *pdwCount = 2;
        return S_OK;
    }
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd) override
    {
        if (!ppcpfd || dwIndex > 1) return E_INVALIDARG;
        *ppcpfd = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(
            CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
        if (!*ppcpfd) return E_OUTOFMEMORY;
        ZeroMemory(*ppcpfd, sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR));
        (*ppcpfd)->dwFieldID = dwIndex;
        (*ppcpfd)->cpft = dwIndex == 0 ? CPFT_EDIT_TEXT : CPFT_PASSWORD_TEXT;
        const wchar_t* label = dwIndex == 0 ? L"账户" : L"密码";
        (*ppcpfd)->pszLabel = static_cast<wchar_t*>(CoTaskMemAlloc((wcslen(label) + 1) * sizeof(wchar_t)));
        if ((*ppcpfd)->pszLabel) wcscpy_s((*ppcpfd)->pszLabel, wcslen(label) + 1, label);
        (*ppcpfd)->guidFieldType = GUID_NULL;
        return S_OK;
    }

    /// 关键: 手机在附近 => 1 个凭据 + 默认 + pbAutoLogon=TRUE => LogonUI 自动登录
    IFACEMETHODIMP GetCredentialCount(DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogon) override
    {
        if (!pdwCount || !pdwDefault || !pbAutoLogon) return E_INVALIDARG;
        *pdwCount = 0;
        *pdwDefault = CREDENTIAL_PROVIDER_NO_DEFAULT;
        *pbAutoLogon = FALSE;

        if (_scenario != CPUS_LOGON && _scenario != CPUS_UNLOCK_WORKSTATION) return S_OK;
        if (!IsProximityNear()) return S_OK;

        Account acc = LoadCredential();
        if (!acc.valid) return S_OK;

        if (!_credential) _credential = new (std::nothrow) CCredential(acc, _scenario);
        if (!_credential) return E_OUTOFMEMORY;

        *pdwCount = 1;
        *pdwDefault = 0;
        *pbAutoLogon = TRUE;
        return S_OK;
    }

    IFACEMETHODIMP GetCredentialAt(DWORD dwIndex, ICredentialProviderCredential** ppcpc) override
    {
        if (!ppcpc || dwIndex != 0 || !_credential) return E_INVALIDARG;
        *ppcpc = _credential;
        _credential->AddRef();
        return S_OK;
    }

private:
    void StartWatcher()
    {
        if (_thread) return;
        _stop = false;
        _thread = CreateThread(nullptr, 0, ThreadProc, this, 0, nullptr);
    }

    void StopWatcher()
    {
        if (!_thread) return;
        _stop = true;
        WaitForSingleObject(_thread, 3000);
        CloseHandle(_thread);
        _thread = nullptr;
    }

    static DWORD WINAPI ThreadProc(LPVOID param)
    {
        auto* self = static_cast<CProvider*>(param);
        while (!self->_stop)
        {
            Sleep(800);
            if (self->_stop) break;
            bool nowNear = IsProximityNear();
            if (nowNear && !self->_wasNear && self->_events)
            {
                // 手机回到附近: 让 LogonUI 重新拉一遍凭据
                self->_events->CredentialsChanged(self->_adviseContext);
            }
            self->_wasNear = nowNear;
        }
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
};

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
    return S_FALSE;   // 常驻, 简化生命周期管理
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(nullptr);
    return TRUE;
}
