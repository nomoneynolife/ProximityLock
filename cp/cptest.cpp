// cptest - 在不注册 COM、不进 LogonUI 的前提下, 直接加载 CP DLL 走一遍完整调用流程
//
// 作用: 安装到系统登录界面之前, 先在普通命令行里验证 CP 的逻辑是否正确
//       (能不能创建、返回几个凭据、是否要自动登录、序列化包结构对不对)
//       这样即使 CP 有 bug, 也只是这个测试程序崩, 不会把登录界面搞卡死
//
// 用法: cptest.exe <DLL路径> [unlock|logon]
//       默认 unlock (对应锁屏解锁场景)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <cstdio>
#include <string>

static const GUID CLSID_ProximityProvider =
    {0x8d2a5e11, 0x3c7b, 0x4f1e, {0x9a, 0x64, 0x5c, 0x0e, 0x2b, 0x7d, 0x9f, 0x31}};

typedef HRESULT(__stdcall* PFN_DllGetClassObject)(REFCLSID, REFIID, void**);

static void PrintHr(const char* what, HRESULT hr)
{
    printf("  %-28s : 0x%08lX %s\n", what, static_cast<unsigned long>(hr),
           SUCCEEDED(hr) ? "OK" : "FAILED");
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        printf("usage: cptest.exe <path-to-ProximityCredentialProvider.dll> [unlock|logon]\n");
        return 2;
    }

    const wchar_t* dllPath = argv[1];
    bool unlock = true;
    if (argc >= 3)
    {
        std::wstring mode = argv[2];
        if (mode == L"logon") unlock = false;
    }

    printf("== ProximityLock CP self-test ==\n");
    printf("DLL  : %ls\n", dllPath);
    printf("场景 : %s\n\n", unlock ? "UNLOCK_WORKSTATION (锁屏解锁)" : "LOGON (开机登录)");

    HRESULT hr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(hr)) { printf("CoInitializeEx failed 0x%08lX\n", static_cast<unsigned long>(hr)); return 1; }

    HMODULE mod = LoadLibraryW(dllPath);
    if (!mod)
    {
        printf("[FAIL] LoadLibrary err=%lu (路径不对? 或者 DLL 是 32 位/依赖缺失)\n", GetLastError());
        CoUninitialize();
        return 1;
    }
    printf("[1] LoadLibrary 成功\n");

    auto pfn = reinterpret_cast<PFN_DllGetClassObject>(GetProcAddress(mod, "DllGetClassObject"));
    if (!pfn) { printf("[FAIL] 找不到 DllGetClassObject 导出\n"); return 1; }

    IClassFactory* factory = nullptr;
    hr = pfn(CLSID_ProximityProvider, IID_IClassFactory, reinterpret_cast<void**>(&factory));
    PrintHr("DllGetClassObject", hr);
    if (FAILED(hr) || !factory) return 1;
    printf("[2] 类工厂就绪\n\n");

    ICredentialProvider* provider = nullptr;
    hr = factory->CreateInstance(nullptr, IID_ICredentialProvider, reinterpret_cast<void**>(&provider));
    PrintHr("CreateInstance", hr);
    factory->Release();
    if (FAILED(hr) || !provider) return 1;
    printf("[3] Provider 创建成功\n\n");

    hr = provider->SetUsageScenario(unlock ? CPUS_UNLOCK_WORKSTATION : CPUS_LOGON, 0);
    PrintHr("SetUsageScenario", hr);

    DWORD fieldCount = 0;
    hr = provider->GetFieldDescriptorCount(&fieldCount);
    printf("  %-28s : %lu 个字段\n", "GetFieldDescriptorCount", fieldCount);
    for (DWORD i = 0; i < fieldCount; i++)
    {
        CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* fd = nullptr;
        if (SUCCEEDED(provider->GetFieldDescriptorAt(i, &fd)) && fd)
        {
            printf("      field %lu: type=%d label=%ls\n", fd->dwFieldID,
                   static_cast<int>(fd->cpft), fd->pszLabel ? fd->pszLabel : L"");
            CoTaskMemFree(fd->pszLabel);
            CoTaskMemFree(fd);
        }
        else
        {
            printf("      field %lu: FAILED\n", i);
        }
    }

    DWORD count = 0, def = 0;
    BOOL autoLogon = FALSE;
    hr = provider->GetCredentialCount(&count, &def, &autoLogon);
    printf("\n");
    PrintHr("GetCredentialCount", hr);
    printf("  %-28s : %lu\n", "凭据数量", count);
    printf("  %-28s : %lu (0x%lX)\n", "默认凭据下标", def, def);
    printf("  %-28s : %s\n", "自动登录", autoLogon ? "TRUE" : "FALSE");

    if (count == 0)
    {
        printf("\n[结论] CP 没有提供凭据, 不会自动登录。常见原因:\n");
        printf("       - state.ini 不存在 / near 不是 1 / ts 超过 6 秒 (先跑 simulate-phone -KeepNear)\n");
        printf("       - creds.bin 不存在或解不开 (跑 setup-credential.ps1)\n");
        printf("       - 找不到当前会话的 LUID (账户名与 creds 里的不一致)\n");
        printf("       - 场景不对 (本工具默认 UNLOCK)\n");
        printf("       详情看 C:\\ProgramData\\ProximityLock\\cp.log\n");
        provider->Release();
        CoUninitialize();
        return 3;
    }

    ICredentialProviderCredential* cred = nullptr;
    hr = provider->GetCredentialAt(def == CREDENTIAL_PROVIDER_NO_DEFAULT ? 0 : def, &cred);
    PrintHr("GetCredentialAt", hr);
    if (FAILED(hr) || !cred) { provider->Release(); CoUninitialize(); return 1; }

    BOOL setAuto = FALSE;
    hr = cred->SetSelected(&setAuto);
    printf("  %-28s : SetSelected 要求自动登录=%s\n", "SetSelected", setAuto ? "TRUE" : "FALSE");

    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE resp{};
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION ser{};
    wchar_t* statusText = nullptr;
    CREDENTIAL_PROVIDER_STATUS_ICON icon = CPSI_NONE;

    printf("\n[4] 调用 GetSerialization ...\n");
    hr = cred->GetSerialization(&resp, &ser, &statusText, &icon);
    PrintHr("GetSerialization", hr);
    printf("  %-28s : %d %s\n", "响应", static_cast<int>(resp),
           resp == CPGSR_RETURN_CREDENTIAL_FINISHED ? "(已返回凭据, 正确)" :
           resp == CPGSR_NO_CREDENTIAL_FINISHED ? "(不出凭据, 交回系统)" : "(未完成)");
    printf("  %-28s : %lu 字节\n", "序列化包长度", ser.cbSerialization);
    printf("  %-28s : %lu\n", "认证包 ID", ser.ulAuthenticationPackage);

    if (resp == CPGSR_RETURN_CREDENTIAL_FINISHED && ser.rgbSerialization && ser.cbSerialization > 0)
    {
        // 按 KERB_INTERACTIVE_UNLOCK_LOGON 解析, 确认三个 UNICODE_STRING 都落在包内
        auto* k = reinterpret_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(ser.rgbSerialization);
        auto inside = [&](const UNICODE_STRING& s) {
            auto* begin = reinterpret_cast<BYTE*>(ser.rgbSerialization);
            auto* end = begin + ser.cbSerialization;
            auto* p = reinterpret_cast<BYTE*>(s.Buffer);
            return p >= begin && p + s.Length <= end;
        };
        printf("\n[5] 包内结构检查 (KERB_INTERACTIVE_UNLOCK_LOGON):\n");
        printf("      LogonId      : %lu:%ld\n", k->LogonId.HighPart, k->LogonId.LowPart);
        printf("      MessageType  : %d (期望 %d=KerbInteractiveLogon)\n", k->Logon.MessageType, KerbInteractiveLogon);
        printf("      UserName     : len=%u 在包内=%s\n", k->Logon.UserName.Length, inside(k->Logon.UserName) ? "是" : "否");
        printf("      DomainName   : len=%u 在包内=%s\n", k->Logon.LogonDomainName.Length, inside(k->Logon.LogonDomainName) ? "是" : "否");
        printf("      Password     : len=%u 在包内=%s\n", k->Logon.Password.Length, inside(k->Logon.Password) ? "是" : "否");
    }

    if (ser.rgbSerialization) CoTaskMemFree(ser.rgbSerialization);
    if (statusText) CoTaskMemFree(statusText);
    cred->Release();
    provider->Release();
    CoUninitialize();
    FreeLibrary(mod);

    printf("\n== 测试结束 (没有修改系统任何设置) ==\n");
    return 0;
}
