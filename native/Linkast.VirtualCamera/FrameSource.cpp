// SPDX-License-Identifier: GPL-3.0-or-later
#include "pch.h"
#include "FrameSource.h"
#include "resource.h"

namespace linkast::vcam
{
    namespace
    {
        int64_t NowUtc()
        {
            FILETIME ft{};
            GetSystemTimePreciseAsFileTime(&ft);
            return (static_cast<int64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
        }

        // Dark navy, the brand background: used if even the placeholder can't be loaded.
        void FillNavy(std::vector<BYTE>& bgra)
        {
            bgra.resize(FrameBytes);
            for (size_t i = 0; i < bgra.size(); i += 4)
            {
                bgra[i] = 0x2A; bgra[i + 1] = 0x14; bgra[i + 2] = 0x12; bgra[i + 3] = 0xFF;
            }
        }
    }

    FrameSource::~FrameSource()
    {
        if (m_header != nullptr)
        {
            UnmapViewOfFile(m_header);
        }

        if (m_section != nullptr)
        {
            CloseHandle(m_section);
        }
    }

    void FrameSource::Initialize(HMODULE module)
    {
        std::lock_guard lock(m_lock);
        m_copy.resize(FrameBytes);
        LoadPlaceholder(module);

        // Full access for SYSTEM, Local Service and administrators; read/write for interactive users
        // (the Linkast app). Medium integrity label so a normal (medium) process may write.
        PSECURITY_DESCRIPTOR sd = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;BA)(A;;GRGW;;;IU)S:(ML;;NW;;;ME)",
                SDDL_REVISION_1, &sd, nullptr))
        {
            return;
        }

        SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };
        m_section = CreateFileMappingW(INVALID_HANDLE_VALUE, &sa, PAGE_READWRITE, 0, SectionSize, SectionName);
        const bool created = m_section != nullptr && GetLastError() != ERROR_ALREADY_EXISTS;
        LocalFree(sd);
        if (m_section == nullptr)
        {
            return;
        }

        m_header = static_cast<Header*>(MapViewOfFile(m_section, FILE_MAP_ALL_ACCESS, 0, 0, SectionSize));
        if (m_header == nullptr)
        {
            CloseHandle(m_section);
            m_section = nullptr;
            return;
        }

        m_frames = reinterpret_cast<BYTE*>(m_header) + HeaderSize;
        if (created || m_header->Magic != Magic)
        {
            ZeroMemory(m_header, HeaderSize);
            m_header->Width = Width;
            m_header->Height = Height;
            m_header->HeaderSize = HeaderSize;
            m_header->FrameBytes = FrameBytes;
            m_header->SlotCount = SlotCount;
            m_header->Version = Version;
            MemoryBarrier();
            m_header->Magic = Magic;
        }
    }

    void FrameSource::LoadPlaceholder(HMODULE module)
    {
        FillNavy(m_placeholder);

        // The placeholder is a PNG resource (rendered from the brand artwork at build time), decoded with WIC.
        HRSRC res = FindResourceW(module, MAKEINTRESOURCEW(IDR_PLACEHOLDER), L"PNG");
        HGLOBAL data = res != nullptr ? LoadResource(module, res) : nullptr;
        const void* bytes = data != nullptr ? LockResource(data) : nullptr;
        const DWORD size = res != nullptr ? SizeofResource(module, res) : 0;
        if (bytes == nullptr || size == 0)
        {
            return;
        }

        const HRESULT init = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        {
            ComPtr<IWICImagingFactory> factory;
            ComPtr<IWICStream> stream;
            ComPtr<IWICBitmapDecoder> decoder;
            ComPtr<IWICBitmapFrameDecode> frame;
            ComPtr<IWICFormatConverter> converter;
            UINT w = 0, h = 0;
            if (SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory)))
                && SUCCEEDED(factory->CreateStream(&stream))
                && SUCCEEDED(stream->InitializeFromMemory(static_cast<BYTE*>(const_cast<void*>(bytes)), size))
                && SUCCEEDED(factory->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnDemand, &decoder))
                && SUCCEEDED(decoder->GetFrame(0, &frame))
                && SUCCEEDED(frame->GetSize(&w, &h)) && w == Width && h == Height
                && SUCCEEDED(factory->CreateFormatConverter(&converter))
                && SUCCEEDED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)))
            {
                (void)converter->CopyPixels(nullptr, Width * 4, FrameBytes, m_placeholder.data());
            }
        }

        if (SUCCEEDED(init))
        {
            CoUninitialize();
        }
    }

    const BYTE* FrameSource::CurrentFrame()
    {
        if (m_header == nullptr || m_header->Magic != Magic)
        {
            return m_placeholder.data();
        }

        const int64_t now = NowUtc();
        m_header->ConsumerTime = now;
        InterlockedIncrement64(&m_header->ConsumerFrames);

        const int64_t sequence = m_header->ProducerSequence;
        if (sequence <= 0 || now - m_header->ProducerTime > StaleAfter100ns)
        {
            return m_placeholder.data();
        }

        // Copy out of the shared slot. The app writes the other slot, publishes it, and only then starts
        // overwriting this one, so the copy is intact if the sequence didn't move while copying. A copy
        // takes well under a millisecond against a 33 ms frame period, so a retry almost never happens.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            const int64_t seq = m_header->ProducerSequence;
            MemoryBarrier();
            memcpy(m_copy.data(), m_frames + (static_cast<size_t>(seq % SlotCount) * FrameBytes), FrameBytes);
            MemoryBarrier();
            if (m_header->ProducerSequence == seq)
            {
                return m_copy.data();
            }
        }

        return m_copy.data(); // best effort: at worst one frame shows a seam
    }

    void FrameSource::Fill(BYTE* scanline0, LONG pitch, bool nv12)
    {
        std::lock_guard lock(m_lock);
        const BYTE* src = CurrentFrame();
        const UINT stride = Width * 4;

        if (!nv12)
        {
            for (UINT y = 0; y < Height; y++)
            {
                memcpy(scanline0 + (static_cast<ptrdiff_t>(y) * pitch), src + (static_cast<size_t>(y) * stride), stride);
            }

            return;
        }

        // BGRA to NV12, BT.601 limited range (what webcams send). Chroma is averaged over 2 x 2.
        BYTE* uvPlane = scanline0 + (static_cast<ptrdiff_t>(pitch) * Height);
        for (UINT y = 0; y < Height; y += 2)
        {
            const BYTE* r0 = src + (static_cast<size_t>(y) * stride);
            const BYTE* r1 = r0 + stride;
            BYTE* y0 = scanline0 + (static_cast<ptrdiff_t>(y) * pitch);
            BYTE* y1 = y0 + pitch;
            BYTE* uv = uvPlane + (static_cast<ptrdiff_t>(y / 2) * pitch);
            for (UINT x = 0; x < Width; x += 2)
            {
                int sumB = 0, sumG = 0, sumR = 0;
                const BYTE* px[4] = { r0 + (x * 4), r0 + (x * 4) + 4, r1 + (x * 4), r1 + (x * 4) + 4 };
                BYTE* out[4] = { y0 + x, y0 + x + 1, y1 + x, y1 + x + 1 };
                for (int i = 0; i < 4; i++)
                {
                    const int b = px[i][0], g = px[i][1], r = px[i][2];
                    *out[i] = static_cast<BYTE>(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                    sumB += b; sumG += g; sumR += r;
                }

                const int b = sumB / 4, g = sumG / 4, r = sumR / 4;
                uv[x] = static_cast<BYTE>(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                uv[x + 1] = static_cast<BYTE>(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
            }
        }
    }
}
