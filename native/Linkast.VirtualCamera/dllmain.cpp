// SPDX-License-Identifier: GPL-3.0-or-later
#include "pch.h"
#include "MediaSource.h"
#pragma warning(push)
#pragma warning(disable : 4324) // padding inside the SDK's own WRL module storage
#include <wrl/module.h>
#pragma warning(pop)

using namespace Microsoft::WRL;

namespace
{
    HMODULE g_module = nullptr;

    // Creates the Activator that Frame Server asks for through the registered CLSID.
    class ActivatorFactory final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IClassFactory>
    {
    public:
        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }

            ComPtr<linkast::vcam::Activator> activator;
            LK_RETURN_IF_FAILED(MakeAndInitialize<linkast::vcam::Activator>(&activator, g_module));
            return activator.CopyTo(riid, object);
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            auto& module = Module<InProc>::GetModule();
            lock ? module.IncrementObjectCount() : module.DecrementObjectCount();
            return S_OK;
        }
    };

    std::wstring ClsidKey()
    {
        return std::wstring(L"Software\\Classes\\CLSID\\") + linkast::vcam::ClsidString;
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }

    *object = nullptr;
    if (clsid != linkast::vcam::Clsid)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto factory = Make<ActivatorFactory>();
    return factory ? factory.CopyTo(riid, object) : E_OUTOFMEMORY;
}

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}

// Registers the media source machine-wide (HKLM): Frame Server runs as a service and can't see
// per-user registrations. Called by LinkastCameraSetup.exe (elevated) or regsvr32.
STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH] = {};
    if (GetModuleFileNameW(g_module, path, MAX_PATH) == 0)
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    HKEY clsidKey = nullptr;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, ClsidKey().c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &clsidKey, nullptr);
    if (status != ERROR_SUCCESS)
    {
        return HRESULT_FROM_WIN32(status);
    }

    status = RegSetValueExW(clsidKey, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(linkast::vcam::FriendlyName), sizeof(linkast::vcam::FriendlyName));
    HKEY serverKey = nullptr;
    if (status == ERROR_SUCCESS)
    {
        status = RegCreateKeyExW(clsidKey, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &serverKey, nullptr);
    }

    if (status == ERROR_SUCCESS)
    {
        status = RegSetValueExW(serverKey, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(path), static_cast<DWORD>((wcslen(path) + 1) * sizeof(wchar_t)));
    }

    if (status == ERROR_SUCCESS)
    {
        constexpr wchar_t model[] = L"Both";
        status = RegSetValueExW(serverKey, L"ThreadingModel", 0, REG_SZ, reinterpret_cast<const BYTE*>(model), sizeof(model));
    }

    if (serverKey != nullptr)
    {
        RegCloseKey(serverKey);
    }

    RegCloseKey(clsidKey);
    return HRESULT_FROM_WIN32(status);
}

STDAPI DllUnregisterServer()
{
    const LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, ClsidKey().c_str());
    return status == ERROR_SUCCESS || status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}
