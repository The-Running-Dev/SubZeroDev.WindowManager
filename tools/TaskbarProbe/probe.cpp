// Calls only documented COM methods. No undocumented message IDs, COM interception or shell changes.
#include <windows.h>
#include <shobjidl.h>
#include <cstdio>
#include <string>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "uuid.lib")

static HANDLE output = INVALID_HANDLE_VALUE;
static ITaskbarList3* taskbar = nullptr;
static HWND app = nullptr;
static UINT buttonCreated = 0;
static bool ready = false;
static bool forced = false;
static bool running = false;
static unsigned step = 0;
static unsigned run = 0;
static ULONGLONG deadline = 0;

static void Log(const char* event, const std::string& detail = "")
{
    SYSTEMTIME now{};
    GetSystemTime(&now);
    LARGE_INTEGER ticks{};
    QueryPerformanceCounter(&ticks);
    char prefix[160]{};
    sprintf_s(prefix, "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ qpc=%lld run=%u ",
        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, ticks.QuadPart, run);
    const std::string line = std::string(prefix) + event + " " + detail + "\r\n";
    DWORD written = 0;
    if (!WriteFile(output, line.data(), static_cast<DWORD>(line.size()), &written, nullptr) || written != line.size())
    {
        // A failed trace cannot be mistaken for an empty successful observation.
        std::fputs("Probe log write failed. Stop the experiment.\n", stderr);
        PostQuitMessage(4);
    }
    std::fputs(line.c_str(), stdout);
}

static std::string Hex(ULONG_PTR value)
{
    char text[32]{};
    sprintf_s(text, "0x%llX", static_cast<unsigned long long>(value));
    return text;
}

static void Result(const char* operation, HRESULT hr)
{
    Log(operation, "hr=" + Hex(static_cast<ULONG>(hr)) + " readiness=" +
        (ready ? "received-TaskbarButtonCreated" : forced ? "FORCED-WITHOUT-READINESS" : "waiting-for-readiness"));
}

static void Snapshot()
{
    const auto tray = FindWindowW(L"Shell_TrayWnd", nullptr);
    DWORD trayPid = 0;
    const auto thread = GetWindowThreadProcessId(tray, &trayPid);
    Log("windows", "client=" + Hex(reinterpret_cast<ULONG_PTR>(app)) + " clientPid=" + std::to_string(GetCurrentProcessId()) +
        " tray=" + Hex(reinterpret_cast<ULONG_PTR>(tray)) + " trayPid=" + std::to_string(trayPid) +
        " trayThread=" + std::to_string(thread) + " shell=" + Hex(reinterpret_cast<ULONG_PTR>(GetShellWindow())));
}

static void Begin(bool force)
{
    if (running) { Log("run-refused", "sequence-already-running"); return; }
    if (!ready && !force) { Log("run-refused", "no-TaskbarButtonCreated; F6-for-forced-native-calls"); return; }
    forced = force && !ready;
    ++run;
    Snapshot();
    Log("sequence-start", forced ? "FORCED-WITHOUT-READINESS; not-a-conforming-client-run" : "conforming-client-run");
    step = 0;
    running = true;
}

static void Tick()
{
    if (!running || taskbar == nullptr) return;
    // One call every two seconds leaves distinct observation windows, including repeated message numbers.
    if (step < 10) Log("call-begin", "step=" + std::to_string(step));
    switch (step++)
    {
    case 0: Result("SetProgressState-none", taskbar->SetProgressState(app, TBPF_NOPROGRESS)); break;
    case 1: Result("SetProgressState-indeterminate", taskbar->SetProgressState(app, TBPF_INDETERMINATE)); break;
    case 2: Result("SetProgressValue-17-of-100", taskbar->SetProgressValue(app, 17, 100)); break;
    case 3: Result("SetProgressValue-61-of-100", taskbar->SetProgressValue(app, 61, 100)); break;
    case 4: Result("SetProgressState-paused", taskbar->SetProgressState(app, TBPF_PAUSED)); break;
    case 5: Result("SetProgressState-error", taskbar->SetProgressState(app, TBPF_ERROR)); break;
    case 6:
        Result("SetOverlayIcon-warning", taskbar->SetOverlayIcon(app, LoadIconW(nullptr, IDI_WARNING), L"Taskbar probe warning"));
        break;
    case 7:
        Result("SetOverlayIcon-information", taskbar->SetOverlayIcon(app, LoadIconW(nullptr, IDI_INFORMATION), L"Taskbar probe information"));
        break;
    case 8: Result("SetOverlayIcon-clear", taskbar->SetOverlayIcon(app, nullptr, L"")); break;
    case 9: Result("SetProgressState-clear", taskbar->SetProgressState(app, TBPF_NOPROGRESS)); break;
    default: running = false; Log("sequence-end"); break;
    }
}

static LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
    if (message == buttonCreated && buttonCreated != 0)
    {
        ready = true;
        Log("TaskbarButtonCreated-received", "message=" + Hex(message) + " wParam=" + Hex(wParam) + " lParam=" + Hex(static_cast<ULONG_PTR>(lParam)));
        // Do not call COM reentrantly from a readiness notification sent by the shell.
        if (!running && run == 0) PostMessageW(window, WM_APP + 1, 0, 0);
        return 0;
    }
    switch (message)
    {
    case WM_APP + 1: Begin(false); return 0;
    case WM_KEYDOWN:
        if (wParam == VK_F5) Begin(false);
        if (wParam == VK_F6) Begin(true);
        if (wParam == VK_ESCAPE) DestroyWindow(window);
        return 0;
    case WM_TIMER:
        if (GetTickCount64() >= deadline) { Log("deadline"); DestroyWindow(window); return 0; }
        Tick(); return 0;
    case WM_DESTROY:
        if (taskbar != nullptr && (ready || forced))
        {
            Log("cleanup-begin");
            Result("cleanup-overlay", taskbar->SetOverlayIcon(window, nullptr, L""));
            Result("cleanup-progress", taskbar->SetProgressState(window, TBPF_NOPROGRESS));
        }
        PostQuitMessage(0); return 0;
    default: return DefWindowProcW(window, message, wParam, lParam);
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2)
    {
        std::fputs("Usage: TaskbarProbe.exe <new-output.log>\nF5: conforming repeat; F6: FORCE without readiness; Esc: exit. Auto-exit: 120s.\n", stderr);
        return 2;
    }
    output = CreateFileW(argv[1], GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (output == INVALID_HANDLE_VALUE) { std::fprintf(stderr, "Cannot create new log: %lu\n", GetLastError()); return 3; }
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    Log("start", "x64 qpcFrequency=" + std::to_string(frequency.QuadPart) + " source=real-ITaskbarList3-calls; no-test-hook");
    const HRESULT init = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    Result("CoInitializeEx", init);
    if (FAILED(init)) { CloseHandle(output); return 5; }
    auto hr = CoCreateInstance(CLSID_TaskbarList, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&taskbar));
    Result("CoCreateInstance-TaskbarList3", hr);
    if (FAILED(hr)) { CoUninitialize(); CloseHandle(output); return 6; }
    hr = taskbar->HrInit();
    Result("HrInit", hr);
    if (FAILED(hr)) { taskbar->Release(); CoUninitialize(); CloseHandle(output); return 7; }
    buttonCreated = RegisterWindowMessageW(L"TaskbarButtonCreated");
    Log("registered-readiness", Hex(buttonCreated));
    const auto instance = GetModuleHandleW(nullptr);
    WNDCLASSW cls{};
    cls.lpfnWndProc = WindowProc;
    cls.hInstance = instance;
    cls.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
    cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    cls.lpszClassName = L"SubZeroDev.TaskbarProbe";
    if (buttonCreated == 0 || RegisterClassW(&cls) == 0)
    {
        Log("setup-failed", std::to_string(GetLastError()));
        taskbar->Release(); CoUninitialize(); CloseHandle(output); return 8;
    }
    app = CreateWindowExW(WS_EX_APPWINDOW, cls.lpszClassName,
        L"Taskbar probe | F5 repeat | F6 force without readiness | Esc close | 120s limit",
        WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, 850, 220, nullptr, nullptr, instance, nullptr);
    if (app == nullptr) { Log("CreateWindow-failed", std::to_string(GetLastError())); taskbar->Release(); CoUninitialize(); CloseHandle(output); return 9; }
    deadline = GetTickCount64() + 120000;
    if (SetTimer(app, 1, 2000, nullptr) == 0)
    {
        Log("SetTimer-failed", std::to_string(GetLastError()));
        DestroyWindow(app); taskbar->Release(); CoUninitialize(); CloseHandle(output); return 10;
    }
    ShowWindow(app, SW_SHOWNORMAL);
    Snapshot();
    Log("waiting", "No API calls until real readiness, unless F6 explicitly forces them. Window closes after 120 seconds.");
    MSG message{};
    BOOL received = 0;
    while ((received = GetMessageW(&message, nullptr, 0, 0)) > 0)
    {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    Log("end", "readinessReceived=" + std::to_string(ready) + " runs=" + std::to_string(run));
    taskbar->Release();
    CoUninitialize();
    CloseHandle(output);
    return received == -1 ? 11 : static_cast<int>(message.wParam);
}
