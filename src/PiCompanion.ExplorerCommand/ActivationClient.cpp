#include "ActivationClient.h"

#include <appmodel.h>
#include <knownfolders.h>
#include <sddl.h>
#include <shlobj_core.h>
#include <strsafe.h>

#include <cstdint>
#include <filesystem>
#include <memory>
#include <string_view>

namespace
{
constexpr DWORD MaximumPayloadBytes = 256 * 1024;
constexpr std::wstring_view PipePrefix = L"\\\\.\\pipe\\PiCompanion.Activation.v1.";

struct LocalMemoryDeleter
{
    void operator()(void* value) const noexcept
    {
        if (value != nullptr)
        {
            LocalFree(value);
        }
    }
};

std::wstring JsonEscape(const std::wstring_view input)
{
    std::wstring result;
    result.reserve(input.size() + 16);
    for (const wchar_t character : input)
    {
        switch (character)
        {
        case L'"': result.append(L"\\\""); break;
        case L'\\': result.append(L"\\\\"); break;
        case L'\b': result.append(L"\\b"); break;
        case L'\f': result.append(L"\\f"); break;
        case L'\n': result.append(L"\\n"); break;
        case L'\r': result.append(L"\\r"); break;
        case L'\t': result.append(L"\\t"); break;
        default:
            if (character < 0x20)
            {
                wchar_t escaped[7]{};
                static_cast<void>(StringCchPrintfW(escaped, ARRAYSIZE(escaped), L"\\u%04x", character));
                result.append(escaped);
            }
            else
            {
                result.push_back(character);
            }
        }
    }

    return result;
}

std::string ToUtf8(const std::wstring_view value)
{
    if (value.empty())
    {
        return {};
    }

    const int byteCount = WideCharToMultiByte(
        CP_UTF8,
        WC_ERR_INVALID_CHARS,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (byteCount <= 0)
    {
        return {};
    }

    std::string result(static_cast<size_t>(byteCount), '\0');
    if (WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            byteCount,
            nullptr,
            nullptr) != byteCount)
    {
        return {};
    }

    return result;
}

std::wstring CreateRequestId()
{
    GUID requestId{};
    if (FAILED(CoCreateGuid(&requestId)))
    {
        return {};
    }

    wchar_t value[39]{};
    if (StringFromGUID2(requestId, value, ARRAYSIZE(value)) <= 0)
    {
        return {};
    }

    std::wstring result(value);
    if (result.size() >= 2 && result.front() == L'{' && result.back() == L'}')
    {
        result = result.substr(1, result.size() - 2);
    }

    return result;
}

std::wstring CreateTimestamp()
{
    SYSTEMTIME timestamp{};
    GetSystemTime(&timestamp);
    wchar_t value[32]{};
    if (FAILED(StringCchPrintfW(
            value,
            ARRAYSIZE(value),
            L"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
            timestamp.wYear,
            timestamp.wMonth,
            timestamp.wDay,
            timestamp.wHour,
            timestamp.wMinute,
            timestamp.wSecond,
            timestamp.wMilliseconds)))
    {
        return {};
    }

    return value;
}

std::string SerializeRequest(const ExplorerSelectionContext& context, const std::wstring& requestId)
{
    std::wstring json = L"{\"protocolVersion\":1,\"requestId\":\"";
    json.append(requestId);
    json.append(L"\",\"workingDirectory\":\"");
    json.append(JsonEscape(context.workingDirectory));
    json.append(L"\",\"selectedPaths\":[");
    for (size_t index = 0; index < context.selectedPaths.size(); ++index)
    {
        if (index != 0)
        {
            json.push_back(L',');
        }

        json.append(L"\"");
        json.append(JsonEscape(context.selectedPaths[index]));
        json.append(L"\"");
    }

    json.append(L"],\"cursorPosition\":");
    if (context.hasCursorPosition)
    {
        json.append(L"{\"x\":");
        json.append(std::to_wstring(context.cursorPosition.x));
        json.append(L",\"y\":");
        json.append(std::to_wstring(context.cursorPosition.y));
        json.push_back(L'}');
    }
    else
    {
        json.append(L"null");
    }

    json.append(L",\"explorerWindowHandle\":");
    json.append(std::to_wstring(reinterpret_cast<std::uintptr_t>(context.explorerWindow)));
    json.append(L",\"invocationKind\":\"");
    json.append(JsonEscape(context.invocationKind));
    json.append(L"\",\"timestamp\":\"");
    json.append(CreateTimestamp());
    json.append(L"\"}");
    return ToUtf8(json);
}

std::wstring GetCurrentUserSid()
{
    HANDLE rawToken = nullptr;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &rawToken) == FALSE)
    {
        return {};
    }

    const std::unique_ptr<void, decltype(&CloseHandle)> token(rawToken, CloseHandle);
    DWORD requiredBytes = 0;
    static_cast<void>(GetTokenInformation(token.get(), TokenUser, nullptr, 0, &requiredBytes));
    if (requiredBytes == 0)
    {
        return {};
    }

    std::vector<std::byte> buffer(requiredBytes);
    if (GetTokenInformation(token.get(), TokenUser, buffer.data(), requiredBytes, &requiredBytes) == FALSE)
    {
        return {};
    }

    const auto tokenUser = reinterpret_cast<const TOKEN_USER*>(buffer.data());
    PWSTR rawSid = nullptr;
    if (ConvertSidToStringSidW(tokenUser->User.Sid, &rawSid) == FALSE)
    {
        return {};
    }

    const std::unique_ptr<void, LocalMemoryDeleter> sid(rawSid);
    return rawSid;
}

bool TrySendToPipe(const std::string& payload)
{
    const std::wstring sid = GetCurrentUserSid();
    if (sid.empty())
    {
        return false;
    }

    const std::wstring pipeName = std::wstring(PipePrefix) + sid;
    HANDLE pipe = CreateFileW(
        pipeName.c_str(),
        GENERIC_WRITE,
        0,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (pipe == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY)
    {
        if (WaitNamedPipeW(pipeName.c_str(), 30) != FALSE)
        {
            pipe = CreateFileW(
                pipeName.c_str(),
                GENERIC_WRITE,
                0,
                nullptr,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
        }
    }

    if (pipe == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    const std::unique_ptr<void, decltype(&CloseHandle)> pipeHandle(pipe, CloseHandle);
    ULONG serverProcessId = 0;
    if (GetNamedPipeServerProcessId(pipeHandle.get(), &serverProcessId) != FALSE &&
        serverProcessId != 0)
    {
        static_cast<void>(AllowSetForegroundWindow(serverProcessId));
    }

    const auto payloadLength = static_cast<std::uint32_t>(payload.size());
    DWORD written = 0;
    if (WriteFile(pipeHandle.get(), &payloadLength, sizeof(payloadLength), &written, nullptr) == FALSE ||
        written != sizeof(payloadLength))
    {
        return false;
    }

    written = 0;
    return WriteFile(
               pipeHandle.get(),
               payload.data(),
               payloadLength,
               &written,
               nullptr) != FALSE &&
        written == payloadLength;
}

std::filesystem::path GetActivationDirectory()
{
    PWSTR rawLocalAppData = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_CREATE, nullptr, &rawLocalAppData)))
    {
        return {};
    }

    const std::filesystem::path localAppData(rawLocalAppData);
    CoTaskMemFree(rawLocalAppData);
    return localAppData / L"PiCompanion" / L"activations";
}

bool WriteActivationFile(
    const std::string& payload,
    const std::wstring& requestId,
    std::filesystem::path& activationFile)
{
    const auto directory = GetActivationDirectory();
    if (directory.empty())
    {
        return false;
    }

    const int directoryResult = SHCreateDirectoryExW(nullptr, directory.c_str(), nullptr);
    if (directoryResult != ERROR_SUCCESS &&
        directoryResult != ERROR_ALREADY_EXISTS &&
        directoryResult != ERROR_FILE_EXISTS)
    {
        return false;
    }

    activationFile = directory / (requestId + L".json");
    HANDLE rawFile = CreateFileW(
        activationFile.c_str(),
        GENERIC_WRITE,
        0,
        nullptr,
        CREATE_NEW,
        FILE_ATTRIBUTE_TEMPORARY,
        nullptr);
    if (rawFile == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    const std::unique_ptr<void, decltype(&CloseHandle)> file(rawFile, CloseHandle);
    DWORD written = 0;
    return WriteFile(
               file.get(),
               payload.data(),
               static_cast<DWORD>(payload.size()),
               &written,
               nullptr) != FALSE &&
        written == payload.size();
}

std::filesystem::path GetModuleDirectory()
{
    HMODULE module = nullptr;
    if (GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<PCWSTR>(&SendExplorerActivation),
            &module) == FALSE)
    {
        return {};
    }

    std::wstring modulePath(32768, L'\0');
    const DWORD length = GetModuleFileNameW(module, modulePath.data(), static_cast<DWORD>(modulePath.size()));
    if (length == 0 || length >= modulePath.size())
    {
        return {};
    }

    modulePath.resize(length);
    return std::filesystem::path(modulePath).parent_path();
}

std::filesystem::path FindDesktopExecutable()
{
    std::wstring configuredPath(32768, L'\0');
    const DWORD configuredLength = GetEnvironmentVariableW(
        L"PI_COMPANION_DESKTOP_EXE",
        configuredPath.data(),
        static_cast<DWORD>(configuredPath.size()));
    if (configuredLength > 0 && configuredLength < configuredPath.size())
    {
        configuredPath.resize(configuredLength);
        if (GetFileAttributesW(configuredPath.c_str()) != INVALID_FILE_ATTRIBUTES)
        {
            return configuredPath;
        }
    }

    const auto sibling = GetModuleDirectory() / L"PiCompanion.Desktop.exe";
    if (GetFileAttributesW(sibling.c_str()) != INVALID_FILE_ATTRIBUTES)
    {
        return sibling;
    }

    UINT32 packagePathLength = 0;
    const LONG packageResult = GetCurrentPackagePath(&packagePathLength, nullptr);
    if (packageResult == ERROR_INSUFFICIENT_BUFFER && packagePathLength > 0)
    {
        std::wstring packagePath(packagePathLength, L'\0');
        if (GetCurrentPackagePath(&packagePathLength, packagePath.data()) == ERROR_SUCCESS)
        {
            packagePath.resize(packagePathLength - 1);
            const auto packagedExecutable = std::filesystem::path(packagePath) / L"PiCompanion.Desktop.exe";
            if (GetFileAttributesW(packagedExecutable.c_str()) != INVALID_FILE_ATTRIBUTES)
            {
                return packagedExecutable;
            }
        }
    }

    return {};
}

bool LaunchDesktop(const std::filesystem::path& activationFile)
{
    const auto executable = FindDesktopExecutable();
    if (executable.empty())
    {
        return false;
    }

    std::wstring commandLine = L"\"" + executable.wstring() + L"\" --activation-file \"" +
        activationFile.wstring() + L"\"";
    STARTUPINFOW startupInfo{sizeof(startupInfo)};
    PROCESS_INFORMATION processInfo{};
    const BOOL started = CreateProcessW(
        executable.c_str(),
        commandLine.data(),
        nullptr,
        nullptr,
        FALSE,
        CREATE_UNICODE_ENVIRONMENT,
        nullptr,
        executable.parent_path().c_str(),
        &startupInfo,
        &processInfo);
    if (started == FALSE)
    {
        return false;
    }

    static_cast<void>(AllowSetForegroundWindow(processInfo.dwProcessId));
    CloseHandle(processInfo.hThread);
    CloseHandle(processInfo.hProcess);
    return true;
}
}

bool SendExplorerActivation(const ExplorerSelectionContext& context) noexcept
{
    try
    {
        const std::wstring requestId = CreateRequestId();
        if (requestId.empty())
        {
            return false;
        }

        const std::string payload = SerializeRequest(context, requestId);
        if (payload.empty() || payload.size() > MaximumPayloadBytes)
        {
            return false;
        }

        if (TrySendToPipe(payload))
        {
            return true;
        }

        std::filesystem::path activationFile;
        if (!WriteActivationFile(payload, requestId, activationFile))
        {
            return false;
        }

        if (LaunchDesktop(activationFile))
        {
            return true;
        }

        static_cast<void>(DeleteFileW(activationFile.c_str()));
        return false;
    }
    catch (...)
    {
        return false;
    }
}
