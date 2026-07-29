#pragma once

#include <shobjidl_core.h>

#include "ActivationClient.h"

HRESULT ResolveSelectionContext(
    IShellItemArray* selectedItems,
    IUnknown* site,
    bool isDirectoryBackground,
    ExplorerSelectionContext& context) noexcept;
