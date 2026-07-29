#pragma once

#include <windows.h>

#include <string>
#include <vector>

struct ExplorerSelectionContext
{
    std::wstring workingDirectory;
    std::vector<std::wstring> selectedPaths;
    POINT cursorPosition{};
    bool hasCursorPosition{false};
    HWND explorerWindow{nullptr};
    std::wstring invocationKind;
};

[[nodiscard]] bool SendExplorerActivation(const ExplorerSelectionContext& context) noexcept;
