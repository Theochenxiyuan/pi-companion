#include "Module.h"

#include <atomic>
#include <new>

namespace
{
std::atomic<long> moduleReferences{0};
HMODULE moduleHandle{nullptr};

class ExplorerCommandClassFactory final : public IClassFactory
{
public:
    ExplorerCommandClassFactory() noexcept
    {
        ModuleAddRef();
    }

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** result) override
    {
        if (result == nullptr)
        {
            return E_POINTER;
        }

        *result = nullptr;
        if (!IsEqualIID(interfaceId, IID_IUnknown) && !IsEqualIID(interfaceId, IID_IClassFactory))
        {
            return E_NOINTERFACE;
        }

        *result = static_cast<IClassFactory*>(this);
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

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID interfaceId, void** result) override
    {
        if (outer != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }

        return CreateExplorerCommand(interfaceId, result);
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        lock != FALSE ? ModuleAddRef() : ModuleRelease();
        return S_OK;
    }

private:
    ~ExplorerCommandClassFactory()
    {
        ModuleRelease();
    }

    std::atomic<ULONG> references_{1};
};
}

void ModuleAddRef() noexcept
{
    ++moduleReferences;
}

void ModuleRelease() noexcept
{
    --moduleReferences;
}

HMODULE ModuleHandle() noexcept
{
    return moduleHandle;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        moduleHandle = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

STDAPI DllCanUnloadNow()
{
    return moduleReferences.load() == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID classId, REFIID interfaceId, void** result)
{
    if (!IsEqualCLSID(classId, CLSID_PiCompanionExplorerCommand))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    if (result == nullptr)
    {
        return E_POINTER;
    }

    *result = nullptr;
    auto* factory = new (std::nothrow) ExplorerCommandClassFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    const HRESULT queryResult = factory->QueryInterface(interfaceId, result);
    factory->Release();
    return queryResult;
}
