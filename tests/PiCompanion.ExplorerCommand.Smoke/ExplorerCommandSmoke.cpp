#include <windows.h>

#include <shlobj_core.h>
#include <shobjidl_core.h>

#include <iostream>
#include <string_view>
#include <vector>

namespace
{
constexpr CLSID PiCompanionCommandClassId = {
    0xa7c1f4c2,
    0x1c6e,
    0x4f57,
    {0x9b, 0x8f, 0x37, 0xa2, 0xc0, 0x9d, 0x6e, 0x11}};

using DllGetClassObjectFunction = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);

template <typename T>
void Release(T*& value) noexcept
{
    if (value != nullptr)
    {
        value->Release();
        value = nullptr;
    }
}

HRESULT CreateItemArray(int argumentCount, wchar_t** arguments, IShellItemArray** result)
{
    *result = nullptr;
    std::vector<PIDLIST_ABSOLUTE> ownedItemIds;
    std::vector<PCIDLIST_ABSOLUTE> itemIds;
    ownedItemIds.reserve(static_cast<size_t>(argumentCount));
    itemIds.reserve(static_cast<size_t>(argumentCount));

    HRESULT createResult = S_OK;
    for (int index = 0; index < argumentCount; ++index)
    {
        PIDLIST_ABSOLUTE itemId = ILCreateFromPathW(arguments[index]);
        if (itemId == nullptr)
        {
            createResult = HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
            break;
        }

        ownedItemIds.push_back(itemId);
        itemIds.push_back(itemId);
    }

    if (SUCCEEDED(createResult))
    {
        createResult = SHCreateShellItemArrayFromIDLists(
            static_cast<UINT>(itemIds.size()),
            itemIds.data(),
            result);
    }

    for (const PIDLIST_ABSOLUTE itemId : ownedItemIds)
    {
        ILFree(itemId);
    }

    return createResult;
}
}

int wmain(const int argumentCount, wchar_t** arguments)
{
    if (argumentCount < 2)
    {
        std::wcerr << L"Usage: ExplorerCommandSmoke <command-dll> [--invoke <path> ...]\n";
        return 2;
    }

    const HRESULT initializeResult = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initializeResult))
    {
        return 3;
    }

    int exitCode = 0;
    const HMODULE module = LoadLibraryExW(arguments[1], nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
    if (module == nullptr)
    {
        CoUninitialize();
        return 4;
    }

    const auto getClassObject = reinterpret_cast<DllGetClassObjectFunction>(
        GetProcAddress(module, "DllGetClassObject"));
    if (getClassObject == nullptr)
    {
        exitCode = 5;
    }

    IClassFactory* factory = nullptr;
    IExplorerCommand* command = nullptr;
    if (exitCode == 0 &&
        FAILED(getClassObject(
            PiCompanionCommandClassId,
            IID_PPV_ARGS(&factory))))
    {
        exitCode = 6;
    }

    if (exitCode == 0 && FAILED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&command))))
    {
        exitCode = 7;
    }

    PWSTR title = nullptr;
    PWSTR icon = nullptr;
    EXPCMDSTATE state = ECS_HIDDEN;
    wchar_t localeName[LOCALE_NAME_MAX_LENGTH]{};
    const bool isChinese = GetUserDefaultLocaleName(localeName, LOCALE_NAME_MAX_LENGTH) > 0 &&
        _wcsnicmp(localeName, L"zh", 2) == 0;
    const std::wstring_view expectedTitle = isChinese ? L"\u8be2\u95ee Pi Companion" : L"Ask Pi Companion";
    if (exitCode == 0 &&
        (FAILED(command->GetTitle(nullptr, &title)) ||
         title == nullptr ||
         std::wstring_view(title) != expectedTitle ||
         FAILED(command->GetIcon(nullptr, &icon)) ||
         icon == nullptr ||
         GetFileAttributesW(icon) == INVALID_FILE_ATTRIBUTES ||
         FAILED(command->GetState(nullptr, FALSE, &state)) ||
         state != ECS_ENABLED))
    {
        exitCode = 8;
    }

    CoTaskMemFree(title);
    CoTaskMemFree(icon);

    if (exitCode == 0 && argumentCount > 2)
    {
        if (std::wstring_view(arguments[2]) != L"--invoke" || argumentCount < 4)
        {
            exitCode = 9;
        }
        else
        {
            IInitializeCommand* initializer = nullptr;
            IShellItemArray* items = nullptr;
            if (FAILED(command->QueryInterface(IID_PPV_ARGS(&initializer))) ||
                FAILED(initializer->Initialize(L"PiCompanionAskAgentFile", nullptr)) ||
                FAILED(CreateItemArray(argumentCount - 3, arguments + 3, &items)) ||
                FAILED(command->Invoke(items, nullptr)))
            {
                exitCode = 10;
            }

            Release(items);
            Release(initializer);
            if (exitCode == 0)
            {
                Sleep(250);
            }
        }
    }

    Release(command);
    Release(factory);
    FreeLibrary(module);
    CoUninitialize();

    if (exitCode == 0)
    {
        std::wcout << L"Explorer command COM smoke test passed.\n";
    }

    return exitCode;
}
