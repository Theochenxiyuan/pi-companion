#include "Module.h"

#include "ActivationClient.h"
#include "SelectionResolver.h"

#include <shlwapi.h>

#include <atomic>
#include <memory>
#include <new>
#include <string>

namespace
{
class ExplorerCommand final : public IExplorerCommand, public IObjectWithSite, public IInitializeCommand
{
public:
    ExplorerCommand() noexcept
    {
        ModuleAddRef();
    }

    ExplorerCommand(const ExplorerCommand&) = delete;
    ExplorerCommand& operator=(const ExplorerCommand&) = delete;

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** result) override
    {
        if (result == nullptr)
        {
            return E_POINTER;
        }

        *result = nullptr;
        if (IsEqualIID(interfaceId, IID_IUnknown) || IsEqualIID(interfaceId, IID_IExplorerCommand))
        {
            *result = static_cast<IExplorerCommand*>(this);
        }
        else if (IsEqualIID(interfaceId, IID_IObjectWithSite))
        {
            *result = static_cast<IObjectWithSite*>(this);
        }
        else if (IsEqualIID(interfaceId, IID_IInitializeCommand))
        {
            *result = static_cast<IInitializeCommand*>(this);
        }
        else
        {
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return ++references_;
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG references = --references_;
        if (references == 0)
        {
            delete this;
        }

        return references;
    }

    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override
    {
        if (title == nullptr)
        {
            return E_POINTER;
        }

        wchar_t localeName[LOCALE_NAME_MAX_LENGTH]{};
        const bool isChinese = GetUserDefaultLocaleName(localeName, LOCALE_NAME_MAX_LENGTH) > 0 &&
            _wcsnicmp(localeName, L"zh", 2) == 0;
        return SHStrDupW(isChinese ? L"\u8be2\u95ee Pi Companion" : L"Ask Pi Companion", title);
    }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        if (icon == nullptr)
        {
            return E_POINTER;
        }

        *icon = nullptr;
        constexpr DWORD pathCapacity = 32768;
        std::wstring iconPath(pathCapacity, L'\0');
        const DWORD pathLength = GetModuleFileNameW(
            ModuleHandle(),
            iconPath.data(),
            pathCapacity);
        if (pathLength == 0)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        if (pathLength >= pathCapacity)
        {
            return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        }

        iconPath.resize(pathLength);
        const size_t separator = iconPath.find_last_of(L"\\/");
        if (separator == std::wstring::npos)
        {
            return E_UNEXPECTED;
        }

        iconPath.resize(separator + 1);
        iconPath.append(L"PiCompanion.ico");
        const DWORD attributes = GetFileAttributesW(iconPath.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        }

        return SHStrDupW(iconPath.c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* toolTip) override
    {
        if (toolTip == nullptr)
        {
            return E_POINTER;
        }

        *toolTip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* commandName) override
    {
        if (commandName == nullptr)
        {
            return E_POINTER;
        }

        *commandName = CLSID_PiCompanionExplorerCommand;
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        if (state == nullptr)
        {
            return E_POINTER;
        }

        *state = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* selectedItems, IBindCtx*) override
    {
        auto context = std::unique_ptr<ExplorerSelectionContext>(new (std::nothrow) ExplorerSelectionContext());
        if (!context)
        {
            return E_OUTOFMEMORY;
        }

        const bool isDirectoryBackground =
            commandName_.find(L"Background") != std::wstring::npos;
        const HRESULT result = ResolveSelectionContext(
            selectedItems,
            site_,
            isDirectoryBackground,
            *context);
        if (FAILED(result))
        {
            return result;
        }

        if (SHCreateThread(
                [](void* state) -> DWORD
                {
                    const std::unique_ptr<ExplorerSelectionContext> activation(
                        static_cast<ExplorerSelectionContext*>(state));
                    static_cast<void>(SendExplorerActivation(*activation));
                    return 0;
                },
                context.get(),
                CTF_COINIT_STA | CTF_PROCESS_REF,
                nullptr) == FALSE)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        static_cast<void>(context.release());
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (flags == nullptr)
        {
            return E_POINTER;
        }

        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        if (commands == nullptr)
        {
            return E_POINTER;
        }

        *commands = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP SetSite(IUnknown* site) override
    {
        if (site != nullptr)
        {
            site->AddRef();
        }

        if (site_ != nullptr)
        {
            site_->Release();
        }

        site_ = site;
        return S_OK;
    }

    IFACEMETHODIMP GetSite(REFIID interfaceId, void** result) override
    {
        if (result == nullptr)
        {
            return E_POINTER;
        }

        *result = nullptr;
        return site_ == nullptr ? E_FAIL : site_->QueryInterface(interfaceId, result);
    }

    IFACEMETHODIMP Initialize(PCWSTR commandName, IPropertyBag*) override
    {
        commandName_ = commandName == nullptr ? L"" : commandName;
        return S_OK;
    }

private:
    ~ExplorerCommand()
    {
        if (site_ != nullptr)
        {
            site_->Release();
        }

        ModuleRelease();
    }

    std::atomic<ULONG> references_{1};
    IUnknown* site_{nullptr};
    std::wstring commandName_;
};
}

HRESULT CreateExplorerCommand(REFIID interfaceId, void** result) noexcept
{
    if (result == nullptr)
    {
        return E_POINTER;
    }

    *result = nullptr;
    auto* command = new (std::nothrow) ExplorerCommand();
    if (command == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    const HRESULT queryResult = command->QueryInterface(interfaceId, result);
    command->Release();
    return queryResult;
}
