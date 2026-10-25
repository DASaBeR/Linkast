// SPDX-License-Identifier: GPL-3.0-or-later
//
// Contract between the Linkast app (producer) and the Linkast Camera media source (consumer,
// loaded by Windows' Frame Server). Mirrored in C# by Linkast.Core.Camera.VirtualCameraProtocol;
// change both together and bump Version.
#pragma once
#include <cstdint>

namespace linkast::vcam
{
    // {F515DB9C-ADFE-4750-B9B7-9648A2FC0706}
    inline constexpr GUID Clsid = { 0xf515db9c, 0xadfe, 0x4750, { 0xb9, 0xb7, 0x96, 0x48, 0xa2, 0xfc, 0x07, 0x06 } };
    inline constexpr wchar_t ClsidString[] = L"{F515DB9C-ADFE-4750-B9B7-9648A2FC0706}";
    inline constexpr wchar_t FriendlyName[] = L"Linkast Camera";

    // Global\ so the app (user session) and Frame Server (session 0) see the same section. Created by
    // the media source (services hold SeCreateGlobalPrivilege; normal users don't).
    inline constexpr wchar_t SectionName[] = L"Global\\LinkastVirtualCamera.v1";

    inline constexpr uint32_t Magic = 0x43564B4C; // "LKVC"
    inline constexpr uint32_t Version = 1;
    inline constexpr uint32_t Width = 1280;
    inline constexpr uint32_t Height = 720;
    inline constexpr uint32_t FrameRate = 30;
    inline constexpr uint32_t HeaderSize = 4096;
    inline constexpr uint32_t FrameBytes = Width * Height * 4; // BGRA, top-down, stride Width * 4
    inline constexpr uint32_t SlotCount = 2;
    inline constexpr uint32_t SectionSize = HeaderSize + (FrameBytes * SlotCount);

    // Frames older than this count as "the app stopped sending" and the placeholder is shown.
    inline constexpr int64_t StaleAfter100ns = 10'000'000; // 1 s

#pragma pack(push, 8)
    struct Header
    {
        uint32_t Magic;
        uint32_t Version;
        uint32_t Width;
        uint32_t Height;
        uint32_t HeaderSize;
        uint32_t FrameBytes;
        uint32_t SlotCount;
        uint32_t Reserved0;
        // Written by the app after a frame is complete; the newest frame is in slot Sequence % SlotCount.
        volatile int64_t ProducerSequence;
        // UTC FILETIME of the last frame written by the app.
        volatile int64_t ProducerTime;
        // UTC FILETIME of the last frame requested by a camera client (Zoom, Teams, ...).
        volatile int64_t ConsumerTime;
        // Number of frames delivered to clients since the section was created.
        volatile int64_t ConsumerFrames;
    };
#pragma pack(pop)

    static_assert(sizeof(Header) == 64, "Header layout is part of the protocol");
}
