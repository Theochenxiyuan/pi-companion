#include "SelectionResolver.h"

#include <filesystem>
#include <shlguid.h>
#include <shlwapi.h>
#include <winrt/base.h>

namespace
{
constexpr DWORD MaximumSelectedPathCount = 64;

HRESULT GetFileSystemPath(IShellItem* item, std::wstring& path) noexcept
{
    if (item == nullptr)
    {
        return E_INVALIDARG;
    }

    PWSTR value = nullptr;
    const HRESULT result = item->GetDisplayName(SIGDN_FILESYSPATH, &value);
    if (SUCCEEDED(result) && value != nullptr)
    {
        path.assign(value);
    }

    CoTaskMemFree(value);
    return result;
}

HRESULT GetExplorerDirectory(IUnknown* site, std::wstring& path) noexcept
{
    if (site == nullptr)
    {
        return E_NOINTERFACE;
    }

    winrt::com_ptr<IServiceProvider> serviceProvider;
    HRESULT result = site->QueryInterface(IID_PPV_ARGS(serviceProvider.put()));
    if (FAILED(result))
    {
        return result;
    }

    winrt::com_ptr<IShellBrowser> browser;
    result = serviceProvider->QueryService(
        SID_STopLevelBrowser,
        IID_PPV_ARGS(browser.put()));
    if (FAILED(result))
    {
        return result;
    }

    winrt::com_ptr<IShellView> view;
    result = browser->QueryActiveShellView(view.put());
    if (FAILED(result))
    {
        return result;
    }

    winrt::com_ptr<IFolderView> folderView;
    result = view->QueryInterface(IID_PPV_ARGS(folderView.put()));
    if (FAILED(result))
    {
        return result;
    }

    winrt::com_ptr<IPersistFolder2> persistFolder;
    result = folderView->GetFolder(IID_PPV_ARGS(persistFolder.put()));
    if (FAILED(result))
    {
        return result;
    }

    PIDLIST_ABSOLUTE folderId = nullptr;
    result = persistFolder->GetCurFolder(&folderId);
    if (FAILED(result))
    {
        return result;
    }

    winrt::com_ptr<IShellItem> folderItem;
    result = SHCreateItemFromIDList(folderId, IID_PPV_ARGS(folderItem.put()));
    CoTaskMemFree(folderId);
    return SUCCEEDED(result) ? GetFileSystemPath(folderItem.get(), path) : result;
}

HRESULT GetSelectedPaths(IShellItemArray* selectedItems, std::vector<std::wstring>& paths) noexcept
{
    if (selectedItems == nullptr)
    {
        return S_OK;
    }

    DWORD count = 0;
    HRESULT result = selectedItems->GetCount(&count);
    if (FAILED(result))
    {
        return result;
    }

    count = min(count, MaximumSelectedPathCount);
    paths.reserve(count);
    for (DWORD index = 0; index < count; ++index)
    {
        winrt::com_ptr<IShellItem> item;
        result = selectedItems->GetItemAt(index, item.put());
        if (FAILED(result))
        {
            continue;
        }

        std::wstring path;
        if (SUCCEEDED(GetFileSystemPath(item.get(), path)) && !path.empty())
        {
            paths.push_back(std::move(path));
        }
    }

    return S_OK;
}

std::wstring GetFallbackWorkingDirectory(const std::vector<std::wstring>& paths, bool isBackground)
{
    if (paths.empty())
    {
        return {};
    }

    if (isBackground)
    {
        return paths.front();
    }

    const std::filesystem::path firstPath(paths.front());
    const auto parent = firstPath.parent_path();
    return parent.empty() ? firstPath.root_path().wstring() : parent.wstring();
}

bool PathsEqual(const std::wstring& left, const std::wstring& right)
{
    const auto normalizedLeft = std::filesystem::path(left).lexically_normal().wstring();
    const auto normalizedRight = std::filesystem::path(right).lexically_normal().wstring();
    return CompareStringOrdinal(
               normalizedLeft.c_str(),
               -1,
               normalizedRight.c_str(),
               -1,
               TRUE) == CSTR_EQUAL;
}
}

HRESULT ResolveSelectionContext(
    IShellItemArray* selectedItems,
    IUnknown* site,
    const bool isDirectoryBackground,
    ExplorerSelectionContext& context) noexcept
{
    try
    {
        context = {};
        context.hasCursorPosition = GetCursorPos(&context.cursorPosition) != FALSE;
        if (site != nullptr)
        {
            static_cast<void>(IUnknown_GetWindow(site, &context.explorerWindow));
        }

        HRESULT result = GetSelectedPaths(selectedItems, context.selectedPaths);
        if (FAILED(result))
        {
            return result;
        }

        static_cast<void>(GetExplorerDirectory(site, context.workingDirectory));
        if (context.workingDirectory.empty())
        {
            context.workingDirectory = GetFallbackWorkingDirectory(
                context.selectedPaths,
                isDirectoryBackground);
        }

        const bool selectedItemIsExplorerDirectory =
            context.selectedPaths.size() == 1 &&
            !context.workingDirectory.empty() &&
            PathsEqual(context.selectedPaths.front(), context.workingDirectory);
        const bool treatAsDirectoryBackground =
            isDirectoryBackground || selectedItemIsExplorerDirectory;

        context.invocationKind =
            treatAsDirectoryBackground ? L"DirectoryBackground" : L"Selection";
        if (treatAsDirectoryBackground)
        {
            context.selectedPaths.clear();
        }

        return context.workingDirectory.empty() ? HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND) : S_OK;
    }
    catch (...)
    {
        return E_FAIL;
    }
}
