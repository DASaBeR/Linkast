// SPDX-License-Identifier: GPL-3.0-or-later
//
// LinkastCameraSetup.exe: installs or removes the Linkast Camera. Runs elevated (manifest), started
// by Linkast only when the user presses "Set up virtual camera" or "Remove".
//
//   LinkastCameraSetup.exe install "<folder containing Linkast.VirtualCamera.dll>"
//   LinkastCameraSetup.exe uninstall
//
// The media source is copied to %ProgramFiles%\Linkast\Virtual Camera: Windows' camera service
// (Frame Server, running as Local Service) loads it, so it must be readable by that account and
// writable only by administrators. Each version gets its own file name, because Frame Server may
// still have the previous one loaded. Progress goes to %TEMP%\LinkastCameraSetup.log.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <knownfolders.h>
#include <shlobj.h>
#include <wrl/client.h>
#include <cstdio>
#include <string>
#include <vector>
#include "..\Linkast.VirtualCamera\VirtualCameraProtocol.h"

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfsensorgroup.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")

using Microsoft::WRL::ComPtr;

namespace
{
    FILE* g_log = nullptr;

    void Log(const wchar_t* format, ...)
    {
        va_list args;
        va_start(args, format);
        vfwprintf(stdout, format, args);
        fwprintf(stdout, L"\n");
        va_end(args);
        if (g_log != nullptr)
        {
            va_start(args, format);
            vfwprintf(g_log, format, args);
            fwprintf(g_log, L"\n");
            fflush(g_log);
            va_end(args);
        }
    }

    std::wstring InstallFolder()
    {
        PWSTR programFiles = nullptr;
        std::wstring folder;
        if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramFiles, 0, nullptr, &programFiles)))
        {
            folder = std::wstring(programFiles) + L"\\Linkast\\Virtual Camera";
        }

        CoTaskMemFree(programFiles);
        return folder;
    }

    // Creates the camera object for our media source; Start/Remove then add or delete the device.
    HRESULT CreateCamera(IMFVirtualCamera** camera)
    {
        return MFCreateVirtualCamera(
            MFVirtualCameraType_SoftwareCameraSource,
            MFVirtualCameraLifetime_System,   // stays listed when Linkast isn't running
            MFVirtualCameraAccess_AllUsers,
            linkast::vcam::FriendlyName,
            linkast::vcam::ClsidString,
            nullptr,
            0,
            camera);
    }

    HRESULT CallRegistration(const std::wstring& dll, const char* function)
    {
        HMODULE module = LoadLibraryExW(dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
        if (module == nullptr)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        using RegisterFn = HRESULT(STDAPICALLTYPE*)();
        auto fn = reinterpret_cast<RegisterFn>(GetProcAddress(module, function));
        const HRESULT hr = fn != nullptr ? fn() : HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
        FreeLibrary(module);
        return hr;
    }

    // Deletes earlier copies; ones still loaded by Frame Server go at the next restart.
    void RemoveOldCopies(const std::wstring& folder, const std::wstring& keep)
    {
        WIN32_FIND_DATAW data{};
        HANDLE find = FindFirstFileW((folder + L"\\Linkast.VirtualCamera-*.dll").c_str(), &data);
        if (find == INVALID_HANDLE_VALUE)
        {
            return;
        }

        do
        {
            const std::wstring path = folder + L"\\" + data.cFileName;
            if (_wcsicmp(path.c_str(), keep.c_str()) != 0 && !DeleteFileW(path.c_str()))
            {
                MoveFileExW(path.c_str(), nullptr, MOVEFILE_DELAY_UNTIL_REBOOT);
                Log(L"Old copy in use, removed at next restart: %s", path.c_str());
            }
        } while (FindNextFileW(find, &data));

        FindClose(find);
    }

    HRESULT Install(const std::wstring& sourceFolder)
    {
        const std::wstring folder = InstallFolder();
        if (folder.empty())
        {
            return E_UNEXPECTED;
        }

        const int created = SHCreateDirectoryExW(nullptr, folder.c_str(), nullptr);
        if (created != ERROR_SUCCESS && created != ERROR_ALREADY_EXISTS)
        {
            Log(L"Can't create %s (error %d)", folder.c_str(), created);
            return HRESULT_FROM_WIN32(created);
        }

        SYSTEMTIME now{};
        GetSystemTime(&now);
        wchar_t stamp[32] = {};
        swprintf_s(stamp, L"%04u%02u%02u%02u%02u%02u", now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond);
        const std::wstring source = sourceFolder + L"\\Linkast.VirtualCamera.dll";
        const std::wstring target = folder + L"\\Linkast.VirtualCamera-" + stamp + L".dll";
        if (!CopyFileW(source.c_str(), target.c_str(), FALSE))
        {
            const DWORD error = GetLastError();
            Log(L"Can't copy %s to %s (error %lu)", source.c_str(), target.c_str(), error);
            return HRESULT_FROM_WIN32(error);
        }

        Log(L"Copied media source to %s", target.c_str());

        HRESULT hr = CallRegistration(target, "DllRegisterServer");
        Log(L"Registered media source: 0x%08X", hr);
        if (FAILED(hr))
        {
            return hr;
        }

        ComPtr<IMFVirtualCamera> camera;
        hr = CreateCamera(&camera);
        if (SUCCEEDED(hr))
        {
            hr = camera->Start(nullptr);
            (void)camera->Shutdown();
        }

        Log(L"Created \"%s\": 0x%08X", linkast::vcam::FriendlyName, hr);
        if (SUCCEEDED(hr))
        {
            RemoveOldCopies(folder, target);
        }

        return hr;
    }

    HRESULT Uninstall()
    {
        ComPtr<IMFVirtualCamera> camera;
        HRESULT hr = CreateCamera(&camera);
        if (SUCCEEDED(hr))
        {
            hr = camera->Remove();
            (void)camera->Shutdown();
        }

        Log(L"Removed \"%s\": 0x%08X", linkast::vcam::FriendlyName, hr);

        const std::wstring key = std::wstring(L"Software\\Classes\\CLSID\\") + linkast::vcam::ClsidString;
        const LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, key.c_str());
        Log(L"Unregistered media source: %ld", status);

        const std::wstring folder = InstallFolder();
        if (!folder.empty())
        {
            RemoveOldCopies(folder, L"");
            if (!RemoveDirectoryW(folder.c_str()))
            {
                MoveFileExW(folder.c_str(), nullptr, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
        }

        // Removing a camera that was never created is fine.
        return SUCCEEDED(hr) || hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) || hr == HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) ? S_OK : hr;
    }
}

int wmain(int argc, wchar_t** argv)
{
    wchar_t temp[MAX_PATH] = {};
    if (GetTempPathW(MAX_PATH, temp) > 0)
    {
        _wfopen_s(&g_log, (std::wstring(temp) + L"LinkastCameraSetup.log").c_str(), L"w, ccs=UTF-8");
    }

    HRESULT hr = E_INVALIDARG;
    if (SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED)) && SUCCEEDED(MFStartup(MF_VERSION)))
    {
        if (argc >= 3 && _wcsicmp(argv[1], L"install") == 0)
        {
            hr = Install(argv[2]);
        }
        else if (argc >= 2 && _wcsicmp(argv[1], L"uninstall") == 0)
        {
            hr = Uninstall();
        }
        else
        {
            Log(L"Usage: LinkastCameraSetup install \"<folder>\" | uninstall");
        }

        MFShutdown();
        CoUninitialize();
    }

    Log(L"Result: 0x%08X", hr);
    if (g_log != nullptr)
    {
        fclose(g_log);
    }

    return SUCCEEDED(hr) ? 0 : static_cast<int>(hr);
}
