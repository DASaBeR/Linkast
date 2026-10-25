// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include "pch.h"

namespace linkast::vcam
{
    // Produces the camera picture: the newest frame Linkast wrote into the shared section, or the
    // "Linkast is not running" placeholder when Linkast isn't sending. Thread-safe.
    class FrameSource
    {
    public:
        FrameSource() = default;
        ~FrameSource();
        FrameSource(const FrameSource&) = delete;
        FrameSource& operator=(const FrameSource&) = delete;

        // Creates (or opens) the shared section and loads the placeholder. Never fails hard: without
        // a section the camera simply shows the placeholder.
        void Initialize(HMODULE module);

        // Writes one frame at Width x Height into a locked 2D buffer (scanline 0 = top row).
        void Fill(BYTE* scanline0, LONG pitch, bool nv12);

    private:
        void LoadPlaceholder(HMODULE module);
        const BYTE* CurrentFrame(); // BGRA, Width * 4 stride

        std::mutex m_lock;
        HANDLE m_section = nullptr;
        Header* m_header = nullptr;
        BYTE* m_frames = nullptr;
        std::vector<BYTE> m_placeholder;
        std::vector<BYTE> m_copy;
    };
}
