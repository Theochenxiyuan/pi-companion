#pragma once

#include <windows.h>

// {A7C1F4C2-1C6E-4F57-9B8F-37A2C09D6E11}
inline constexpr CLSID CLSID_PiCompanionExplorerCommand = {
    0xa7c1f4c2,
    0x1c6e,
    0x4f57,
    {0x9b, 0x8f, 0x37, 0xa2, 0xc0, 0x9d, 0x6e, 0x11}};

void ModuleAddRef() noexcept;
void ModuleRelease() noexcept;
HMODULE ModuleHandle() noexcept;
HRESULT CreateExplorerCommand(REFIID interfaceId, void** result) noexcept;
