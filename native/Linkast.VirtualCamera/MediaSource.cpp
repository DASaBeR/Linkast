// SPDX-License-Identifier: GPL-3.0-or-later
#include "pch.h"
#include "MediaSource.h"

namespace linkast::vcam
{
    namespace
    {
        constexpr LONGLONG FrameDuration100ns = 10'000'000 / FrameRate;

        HRESULT CreateVideoType(REFGUID subtype, UINT32 bitsPerPixel, IMFMediaType** type)
        {
            ComPtr<IMFMediaType> mt;
            LK_RETURN_IF_FAILED(MFCreateMediaType(&mt));
            LK_RETURN_IF_FAILED(mt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
            LK_RETURN_IF_FAILED(mt->SetGUID(MF_MT_SUBTYPE, subtype));
            LK_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            LK_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
            LK_RETURN_IF_FAILED(MFSetAttributeSize(mt.Get(), MF_MT_FRAME_SIZE, Width, Height));
            LK_RETURN_IF_FAILED(MFSetAttributeRatio(mt.Get(), MF_MT_FRAME_RATE, FrameRate, 1));
            LK_RETURN_IF_FAILED(MFSetAttributeRatio(mt.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            LK_RETURN_IF_FAILED(mt->SetUINT32(MF_MT_AVG_BITRATE, Width * Height * bitsPerPixel * FrameRate));
            *type = mt.Detach();
            return S_OK;
        }

        HRESULT SetCaptureStreamAttributes(IMFAttributes* attributes, DWORD streamId)
        {
            LK_RETURN_IF_FAILED(attributes->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
            LK_RETURN_IF_FAILED(attributes->SetUINT32(MF_DEVICESTREAM_STREAM_ID, streamId));
            LK_RETURN_IF_FAILED(attributes->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
            LK_RETURN_IF_FAILED(attributes->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));
            return S_OK;
        }
    }

    // ---- MediaStream -------------------------------------------------------------------------

    HRESULT MediaStream::RuntimeClassInitialize(MediaSource* source, DWORD streamId, FrameSource* frames)
    {
        std::lock_guard lock(m_lock);
        m_source = source;
        m_id = streamId;
        m_frames = frames;

        // NV12 first: it is what most camera clients ask for. RGB32 for clients that prefer it.
        IMFMediaType* types[2] = {};
        LK_RETURN_IF_FAILED(CreateVideoType(MFVideoFormat_NV12, 12, &types[0]));
        HRESULT hr = CreateVideoType(MFVideoFormat_RGB32, 32, &types[1]);
        if (SUCCEEDED(hr))
        {
            hr = MFCreateStreamDescriptor(m_id, 2, types, &m_descriptor);
        }

        if (SUCCEEDED(hr))
        {
            ComPtr<IMFMediaTypeHandler> handler;
            hr = m_descriptor->GetMediaTypeHandler(&handler);
            if (SUCCEEDED(hr))
            {
                hr = handler->SetCurrentMediaType(types[0]);
            }
        }

        for (auto* t : types)
        {
            if (t != nullptr)
            {
                t->Release();
            }
        }

        LK_RETURN_IF_FAILED(hr);
        LK_RETURN_IF_FAILED(SetCaptureStreamAttributes(m_descriptor.Get(), m_id));
        LK_RETURN_IF_FAILED(MFCreateAttributes(&m_attributes, 4));
        LK_RETURN_IF_FAILED(SetCaptureStreamAttributes(m_attributes.Get(), m_id));
        LK_RETURN_IF_FAILED(MFCreateEventQueue(&m_events));
        return S_OK;
    }

    HRESULT MediaStream::CheckShutdown() const
    {
        if (m_shutdown)
        {
            return MF_E_SHUTDOWN;
        }

        return m_events == nullptr ? E_UNEXPECTED : S_OK;
    }

    IFACEMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_events->BeginGetEvent(callback, state);
    }

    IFACEMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_events->EndGetEvent(result, event);
    }

    IFACEMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event)
    {
        // GetEvent may block, so the lock is not held while waiting.
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard lock(m_lock);
            LK_RETURN_IF_FAILED(CheckShutdown());
            queue = m_events;
        }

        return queue->GetEvent(flags, event);
    }

    IFACEMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* item)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_events->QueueEventParamVar(type, extendedType, status, item);
    }

    IFACEMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source)
    {
        if (source == nullptr)
        {
            return E_POINTER;
        }

        *source = nullptr;
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_source.CopyTo(source);
    }

    IFACEMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** descriptor)
    {
        if (descriptor == nullptr)
        {
            return E_POINTER;
        }

        *descriptor = nullptr;
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_descriptor ? m_descriptor.CopyTo(descriptor) : E_UNEXPECTED;
    }

    IFACEMETHODIMP MediaStream::RequestSample(IUnknown* token)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        if (m_state != MF_STREAM_STATE_RUNNING || m_allocator == nullptr)
        {
            return MF_E_INVALIDREQUEST;
        }

        ComPtr<IMFSample> sample;
        ComPtr<IMFMediaBuffer> buffer;
        ComPtr<IMF2DBuffer2> buffer2d;
        LK_RETURN_IF_FAILED(m_allocator->AllocateSample(&sample));
        LK_RETURN_IF_FAILED(sample->GetBufferByIndex(0, &buffer));
        LK_RETURN_IF_FAILED(buffer.As(&buffer2d));

        BYTE* scanline0 = nullptr;
        BYTE* start = nullptr;
        LONG pitch = 0;
        DWORD length = 0;
        LK_RETURN_IF_FAILED(buffer2d->Lock2DSize(MF2DBuffer_LockFlags_Write, &scanline0, &pitch, &start, &length));
        m_frames->Fill(scanline0, pitch, m_isNv12);
        LK_RETURN_IF_FAILED(buffer2d->Unlock2D());

        LK_RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
        LK_RETURN_IF_FAILED(sample->SetSampleDuration(FrameDuration100ns));
        if (token != nullptr)
        {
            LK_RETURN_IF_FAILED(sample->SetUnknown(MFSampleExtension_Token, token));
        }

        return m_events->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
    }

    IFACEMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE state)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        if (m_state == state)
        {
            return S_OK;
        }

        switch (state)
        {
        case MF_STREAM_STATE_PAUSED:
            if (m_state != MF_STREAM_STATE_RUNNING)
            {
                return MF_E_INVALID_STATE_TRANSITION;
            }

            m_state = MF_STREAM_STATE_PAUSED;
            return S_OK;
        case MF_STREAM_STATE_RUNNING:
            return StartInternal(false, nullptr);
        case MF_STREAM_STATE_STOPPED:
            return StopInternal(false);
        default:
            return MF_E_INVALID_STATE_TRANSITION;
        }
    }

    IFACEMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* state)
    {
        if (state == nullptr)
        {
            return E_POINTER;
        }

        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        *state = m_state;
        return S_OK;
    }

    HRESULT MediaStream::Start(IMFMediaType* mediaType)
    {
        if (mediaType == nullptr)
        {
            return E_INVALIDARG;
        }

        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return StartInternal(true, mediaType);
    }

    HRESULT MediaStream::Stop(bool sendEvent)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return StopInternal(sendEvent);
    }

    HRESULT MediaStream::StartInternal(bool sendEvent, IMFMediaType* mediaType)
    {
        if (mediaType != nullptr)
        {
            m_mediaType = mediaType;
        }

        if (m_mediaType == nullptr)
        {
            return MF_E_NOT_INITIALIZED;
        }

        if (m_allocator == nullptr)
        {
            // Frame Server normally provides an allocator; create one if it didn't.
            LK_RETURN_IF_FAILED(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&m_allocator)));
        }

        GUID subtype = GUID_NULL;
        LK_RETURN_IF_FAILED(m_mediaType->GetGUID(MF_MT_SUBTYPE, &subtype));
        m_isNv12 = subtype == MFVideoFormat_NV12;
        LK_RETURN_IF_FAILED(m_allocator->InitializeSampleAllocator(10, m_mediaType.Get()));

        if (sendEvent)
        {
            LK_RETURN_IF_FAILED(m_events->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
        }

        m_state = MF_STREAM_STATE_RUNNING;
        return S_OK;
    }

    HRESULT MediaStream::StopInternal(bool sendEvent)
    {
        m_state = MF_STREAM_STATE_STOPPED;
        if (sendEvent)
        {
            LK_RETURN_IF_FAILED(m_events->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
        }

        return S_OK;
    }

    HRESULT MediaStream::Shutdown()
    {
        std::lock_guard lock(m_lock);
        m_shutdown = true;
        m_source.Reset();
        if (m_events != nullptr)
        {
            (void)m_events->Shutdown();
            m_events.Reset();
        }

        m_attributes.Reset();
        m_descriptor.Reset();
        m_allocator.Reset();
        m_state = MF_STREAM_STATE_STOPPED;
        return S_OK;
    }

    HRESULT MediaStream::SetAllocator(IMFVideoSampleAllocator* allocator)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        if (m_state == MF_STREAM_STATE_RUNNING)
        {
            return MF_E_INVALIDREQUEST;
        }

        m_allocator = allocator;
        return S_OK;
    }

    HRESULT MediaStream::GetAttributes(IMFAttributes** attributes)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_attributes.CopyTo(attributes);
    }

    // ---- MediaSource -------------------------------------------------------------------------

    HRESULT MediaSource::RuntimeClassInitialize(IMFAttributes* activateAttributes, HMODULE module)
    {
        std::lock_guard lock(m_lock);
        if (m_state != State::Invalid)
        {
            return MF_E_ALREADY_INITIALIZED;
        }

        m_frames.Initialize(module);
        LK_RETURN_IF_FAILED(CreateSourceAttributes(activateAttributes));
        LK_RETURN_IF_FAILED(MFCreateEventQueue(&m_events));
        LK_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaStream>(&m_stream, this, 0, &m_frames));

        ComPtr<IMFStreamDescriptor> descriptor;
        LK_RETURN_IF_FAILED(m_stream->GetStreamDescriptor(&descriptor));
        IMFStreamDescriptor* list[1] = { descriptor.Get() };
        LK_RETURN_IF_FAILED(MFCreatePresentationDescriptor(1, list, &m_descriptor));
        m_state = State::Stopped;
        return S_OK;
    }

    HRESULT MediaSource::CreateSourceAttributes(IMFAttributes* activateAttributes)
    {
        LK_RETURN_IF_FAILED(MFCreateAttributes(&m_attributes, 4));
        if (activateAttributes != nullptr)
        {
            LK_RETURN_IF_FAILED(activateAttributes->CopyAllItems(m_attributes.Get()));
        }

        // A legacy sensor profile is mandatory so non-profile-aware apps can use the camera.
        ComPtr<IMFSensorProfileCollection> profiles;
        ComPtr<IMFSensorProfile> profile;
        LK_RETURN_IF_FAILED(MFCreateSensorProfileCollection(&profiles));
        LK_RETURN_IF_FAILED(MFCreateSensorProfile(KSCAMERAPROFILE_Legacy, 0, nullptr, &profile));
        LK_RETURN_IF_FAILED(profile->AddProfileFilter(0, L"((RES==;FRT<=30,1;SUT==))"));
        LK_RETURN_IF_FAILED(profiles->AddProfile(profile.Get()));
        LK_RETURN_IF_FAILED(m_attributes->SetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, profiles.Get()));
        return S_OK;
    }

    HRESULT MediaSource::CheckShutdown() const
    {
        if (m_state == State::Shutdown)
        {
            return MF_E_SHUTDOWN;
        }

        return m_events == nullptr || m_stream == nullptr ? E_UNEXPECTED : S_OK;
    }

    IFACEMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_events->BeginGetEvent(callback, state);
    }

    IFACEMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_events->EndGetEvent(result, event);
    }

    IFACEMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event)
    {
        ComPtr<IMFMediaEventQueue> queue;
        {
            std::lock_guard lock(m_lock);
            LK_RETURN_IF_FAILED(CheckShutdown());
            queue = m_events;
        }

        return queue->GetEvent(flags, event);
    }

    IFACEMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* item)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_events->QueueEventParamVar(type, extendedType, status, item);
    }

    IFACEMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor)
    {
        if (descriptor == nullptr)
        {
            return E_POINTER;
        }

        *descriptor = nullptr;
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        return m_descriptor->Clone(descriptor);
    }

    IFACEMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics)
    {
        if (characteristics == nullptr)
        {
            return E_POINTER;
        }

        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        *characteristics = MFMEDIASOURCE_IS_LIVE;
        return S_OK;
    }

    IFACEMETHODIMP MediaSource::Pause()
    {
        return MF_E_INVALID_STATE_TRANSITION;
    }

    IFACEMETHODIMP MediaSource::Shutdown()
    {
        ComPtr<MediaStream> stream;
        {
            std::lock_guard lock(m_lock);
            m_state = State::Shutdown;
            m_attributes.Reset();
            m_descriptor.Reset();
            if (m_events != nullptr)
            {
                (void)m_events->Shutdown();
                m_events.Reset();
            }

            stream = m_stream;
            m_stream.Reset();
        }

        if (stream != nullptr)
        {
            (void)stream->Shutdown();
        }

        return S_OK;
    }

    IFACEMETHODIMP MediaSource::Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition)
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        if (descriptor == nullptr || startPosition == nullptr)
        {
            return E_INVALIDARG;
        }

        if (timeFormat != nullptr && *timeFormat != GUID_NULL)
        {
            return MF_E_UNSUPPORTED_TIME_FORMAT;
        }

        DWORD count = 0;
        LK_RETURN_IF_FAILED(descriptor->GetStreamDescriptorCount(&count));
        if (count != 1)
        {
            return E_INVALIDARG;
        }

        PROPVARIANT startTime;
        LK_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &startTime));

        BOOL selected = FALSE;
        ComPtr<IMFStreamDescriptor> streamDescriptor;
        LK_RETURN_IF_FAILED(descriptor->GetStreamDescriptorByIndex(0, &selected, &streamDescriptor));

        BOOL wasSelected = FALSE;
        ComPtr<IMFStreamDescriptor> ours;
        LK_RETURN_IF_FAILED(m_descriptor->GetStreamDescriptorByIndex(0, &wasSelected, &ours));

        if (selected)
        {
            LK_RETURN_IF_FAILED(m_descriptor->SelectStream(0));
            ComPtr<IMFMediaTypeHandler> handler;
            ComPtr<IMFMediaType> type;
            LK_RETURN_IF_FAILED(streamDescriptor->GetMediaTypeHandler(&handler));
            LK_RETURN_IF_FAILED(handler->GetCurrentMediaType(&type));

            ComPtr<IUnknown> unknown;
            LK_RETURN_IF_FAILED(m_stream.As(&unknown));
            LK_RETURN_IF_FAILED(m_events->QueueEventParamUnk(wasSelected ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK, unknown.Get()));
            LK_RETURN_IF_FAILED(m_stream->Start(type.Get()));
        }
        else if (wasSelected)
        {
            LK_RETURN_IF_FAILED(m_descriptor->DeselectStream(0));
            LK_RETURN_IF_FAILED(m_stream->Stop(false));
        }

        LK_RETURN_IF_FAILED(m_events->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &startTime));
        m_state = State::Started;
        return S_OK;
    }

    IFACEMETHODIMP MediaSource::Stop()
    {
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        if (m_state != State::Started)
        {
            return MF_E_INVALID_STATE_TRANSITION;
        }

        m_state = State::Stopped;
        PROPVARIANT stopTime;
        LK_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &stopTime));
        LK_RETURN_IF_FAILED(m_stream->Stop(true));
        LK_RETURN_IF_FAILED(m_descriptor->DeselectStream(0));
        return m_events->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, &stopTime);
    }

    IFACEMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes)
    {
        if (attributes == nullptr)
        {
            return E_POINTER;
        }

        *attributes = nullptr;
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        // Must return a reference to the source's own attribute store.
        return m_attributes.CopyTo(attributes);
    }

    IFACEMETHODIMP MediaSource::GetStreamAttributes(DWORD streamId, IMFAttributes** attributes)
    {
        if (attributes == nullptr)
        {
            return E_POINTER;
        }

        *attributes = nullptr;
        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        if (streamId != m_stream->Id())
        {
            return MF_E_NOT_FOUND;
        }

        return m_stream->GetAttributes(attributes);
    }

    IFACEMETHODIMP MediaSource::SetD3DManager(IUnknown*)
    {
        // Frames are produced in system memory.
        return E_NOTIMPL;
    }

    IFACEMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* object)
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        *object = nullptr;
        return MF_E_UNSUPPORTED_SERVICE;
    }

    IFACEMETHODIMP MediaSource::KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID, ULONG, ULONG*)
    {
        // ERROR_SET_NOT_FOUND is what a camera driver returns for controls it doesn't have.
        return property == nullptr || propertyLength < sizeof(KSPROPERTY) ? E_INVALIDARG : HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    IFACEMETHODIMP MediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*)
    {
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    IFACEMETHODIMP MediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*)
    {
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    IFACEMETHODIMP MediaSource::SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator)
    {
        if (allocator == nullptr)
        {
            return E_POINTER;
        }

        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        if (outputStreamId != m_stream->Id())
        {
            return MF_E_NOT_FOUND;
        }

        ComPtr<IMFVideoSampleAllocator> videoAllocator;
        LK_RETURN_IF_FAILED(allocator->QueryInterface(IID_PPV_ARGS(&videoAllocator)));
        return m_stream->SetAllocator(videoAllocator.Get());
    }

    IFACEMETHODIMP MediaSource::GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage)
    {
        if (inputStreamId == nullptr || usage == nullptr)
        {
            return E_POINTER;
        }

        std::lock_guard lock(m_lock);
        LK_RETURN_IF_FAILED(CheckShutdown());
        if (outputStreamId != m_stream->Id())
        {
            return MF_E_NOT_FOUND;
        }

        *inputStreamId = outputStreamId;
        *usage = MFSampleAllocatorUsage_UsesProvidedAllocator;
        return S_OK;
    }

    // ---- Activator ---------------------------------------------------------------------------

    HRESULT Activator::RuntimeClassInitialize(HMODULE module)
    {
        m_module = module;
        return MFCreateAttributes(&m_attributes, 2);
    }

    IFACEMETHODIMP Activator::ActivateObject(REFIID riid, void** object)
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        *object = nullptr;

        // A client may have shut the source down directly; activate a fresh one then.
        DWORD characteristics = 0;
        if (m_source != nullptr && m_source->GetCharacteristics(&characteristics) == MF_E_SHUTDOWN)
        {
            m_source.Reset();
        }

        if (m_source == nullptr)
        {
            LK_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaSource>(&m_source, m_attributes.Get(), m_module));
        }

        return m_source.CopyTo(riid, object);
    }

    IFACEMETHODIMP Activator::ShutdownObject()
    {
        if (m_source != nullptr)
        {
            (void)m_source->Shutdown();
            m_source.Reset();
        }

        return S_OK;
    }

    IFACEMETHODIMP Activator::DetachObject()
    {
        m_source.Reset();
        return S_OK;
    }
}
