// SPDX-License-Identifier: GPL-3.0-or-later
//
// Media Foundation media source for the Linkast Camera. The structure (source, stream, activator,
// sample allocator handling, stream attributes) follows Microsoft's VirtualCamera sample
// (github.com/microsoft/Windows-Camera, MIT license); see THIRD-PARTY-NOTICES.md.
#pragma once
#include "pch.h"
#include "FrameSource.h"

namespace linkast::vcam
{
    class MediaSource;

    class MediaStream final : public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
        Microsoft::WRL::ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>>
    {
    public:
        HRESULT RuntimeClassInitialize(MediaSource* source, DWORD streamId, FrameSource* frames);

        // IMFMediaEventGenerator
        IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
        IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
        IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
        IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* item) override;

        // IMFMediaStream
        IFACEMETHODIMP GetMediaSource(IMFMediaSource** source) override;
        IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
        IFACEMETHODIMP RequestSample(IUnknown* token) override;

        // IMFMediaStream2
        IFACEMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
        IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

        HRESULT Start(IMFMediaType* mediaType);
        HRESULT Stop(bool sendEvent);
        HRESULT Shutdown();
        HRESULT SetAllocator(IMFVideoSampleAllocator* allocator);
        HRESULT GetAttributes(IMFAttributes** attributes);
        DWORD Id() const { return m_id; }

    private:
        HRESULT CheckShutdown() const;
        HRESULT StartInternal(bool sendEvent, IMFMediaType* mediaType);
        HRESULT StopInternal(bool sendEvent);

        std::mutex m_lock;
        ComPtr<IMFMediaSource> m_source;
        ComPtr<IMFMediaEventQueue> m_events;
        ComPtr<IMFAttributes> m_attributes;
        ComPtr<IMFStreamDescriptor> m_descriptor;
        ComPtr<IMFVideoSampleAllocator> m_allocator;
        ComPtr<IMFMediaType> m_mediaType;
        FrameSource* m_frames = nullptr;
        MF_STREAM_STATE m_state = MF_STREAM_STATE_STOPPED;
        DWORD m_id = 0;
        bool m_isNv12 = true;
        bool m_shutdown = false;
    };

    class MediaSource final : public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
        Microsoft::WRL::ChainInterfaces<IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
        IMFGetService, IKsControl, IMFSampleAllocatorControl>
    {
    public:
        HRESULT RuntimeClassInitialize(IMFAttributes* activateAttributes, HMODULE module);

        // IMFMediaEventGenerator
        IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
        IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
        IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
        IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* item) override;

        // IMFMediaSource
        IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** descriptor) override;
        IFACEMETHODIMP GetCharacteristics(DWORD* characteristics) override;
        IFACEMETHODIMP Pause() override;
        IFACEMETHODIMP Shutdown() override;
        IFACEMETHODIMP Start(IMFPresentationDescriptor* descriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) override;
        IFACEMETHODIMP Stop() override;

        // IMFMediaSourceEx
        IFACEMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
        IFACEMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) override;
        IFACEMETHODIMP SetD3DManager(IUnknown* manager) override;

        // IMFGetService
        IFACEMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* object) override;

        // IKsControl: no custom camera controls.
        IFACEMETHODIMP KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID data, ULONG dataLength, ULONG* returned) override;
        IFACEMETHODIMP KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID data, ULONG dataLength, ULONG* returned) override;
        IFACEMETHODIMP KsEvent(PKSEVENT event, ULONG eventLength, LPVOID data, ULONG dataLength, ULONG* returned) override;

        // IMFSampleAllocatorControl
        IFACEMETHODIMP SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator) override;
        IFACEMETHODIMP GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage) override;

    private:
        enum class State { Invalid, Stopped, Started, Shutdown };

        HRESULT CheckShutdown() const;
        HRESULT CreateSourceAttributes(IMFAttributes* activateAttributes);

        std::mutex m_lock;
        State m_state = State::Invalid;
        ComPtr<IMFMediaEventQueue> m_events;
        ComPtr<IMFPresentationDescriptor> m_descriptor;
        ComPtr<IMFAttributes> m_attributes;
        ComPtr<MediaStream> m_stream;
        FrameSource m_frames;
    };

    // What Frame Server creates from the registered CLSID; it activates the media source on demand.
    class Activator final : public Microsoft::WRL::RuntimeClass<
        Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
        Microsoft::WRL::ChainInterfaces<IMFActivate, IMFAttributes>>
    {
    public:
        HRESULT RuntimeClassInitialize(HMODULE module);

        IFACEMETHODIMP ActivateObject(REFIID riid, void** object) override;
        IFACEMETHODIMP ShutdownObject() override;
        IFACEMETHODIMP DetachObject() override;

        // IMFAttributes, forwarded to an attribute store.
        IFACEMETHODIMP GetItem(REFGUID key, PROPVARIANT* item) override { return m_attributes->GetItem(key, item); }
        IFACEMETHODIMP GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) override { return m_attributes->GetItemType(key, type); }
        IFACEMETHODIMP CompareItem(REFGUID key, REFPROPVARIANT item, BOOL* result) override { return m_attributes->CompareItem(key, item, result); }
        IFACEMETHODIMP Compare(IMFAttributes* theirs, MF_ATTRIBUTES_MATCH_TYPE type, BOOL* result) override { return m_attributes->Compare(theirs, type, result); }
        IFACEMETHODIMP GetUINT32(REFGUID key, UINT32* item) override { return m_attributes->GetUINT32(key, item); }
        IFACEMETHODIMP GetUINT64(REFGUID key, UINT64* item) override { return m_attributes->GetUINT64(key, item); }
        IFACEMETHODIMP GetDouble(REFGUID key, double* item) override { return m_attributes->GetDouble(key, item); }
        IFACEMETHODIMP GetGUID(REFGUID key, GUID* item) override { return m_attributes->GetGUID(key, item); }
        IFACEMETHODIMP GetStringLength(REFGUID key, UINT32* length) override { return m_attributes->GetStringLength(key, length); }
        IFACEMETHODIMP GetString(REFGUID key, LPWSTR item, UINT32 size, UINT32* length) override { return m_attributes->GetString(key, item, size, length); }
        IFACEMETHODIMP GetAllocatedString(REFGUID key, LPWSTR* item, UINT32* length) override { return m_attributes->GetAllocatedString(key, item, length); }
        IFACEMETHODIMP GetBlobSize(REFGUID key, UINT32* size) override { return m_attributes->GetBlobSize(key, size); }
        IFACEMETHODIMP GetBlob(REFGUID key, UINT8* buffer, UINT32 size, UINT32* blobSize) override { return m_attributes->GetBlob(key, buffer, size, blobSize); }
        IFACEMETHODIMP GetAllocatedBlob(REFGUID key, UINT8** buffer, UINT32* size) override { return m_attributes->GetAllocatedBlob(key, buffer, size); }
        IFACEMETHODIMP GetUnknown(REFGUID key, REFIID riid, LPVOID* item) override { return m_attributes->GetUnknown(key, riid, item); }
        IFACEMETHODIMP SetItem(REFGUID key, REFPROPVARIANT item) override { return m_attributes->SetItem(key, item); }
        IFACEMETHODIMP DeleteItem(REFGUID key) override { return m_attributes->DeleteItem(key); }
        IFACEMETHODIMP DeleteAllItems() override { return m_attributes->DeleteAllItems(); }
        IFACEMETHODIMP SetUINT32(REFGUID key, UINT32 item) override { return m_attributes->SetUINT32(key, item); }
        IFACEMETHODIMP SetUINT64(REFGUID key, UINT64 item) override { return m_attributes->SetUINT64(key, item); }
        IFACEMETHODIMP SetDouble(REFGUID key, double item) override { return m_attributes->SetDouble(key, item); }
        IFACEMETHODIMP SetGUID(REFGUID key, REFGUID item) override { return m_attributes->SetGUID(key, item); }
        IFACEMETHODIMP SetString(REFGUID key, LPCWSTR item) override { return m_attributes->SetString(key, item); }
        IFACEMETHODIMP SetBlob(REFGUID key, const UINT8* buffer, UINT32 size) override { return m_attributes->SetBlob(key, buffer, size); }
        IFACEMETHODIMP SetUnknown(REFGUID key, IUnknown* item) override { return m_attributes->SetUnknown(key, item); }
        IFACEMETHODIMP LockStore() override { return m_attributes->LockStore(); }
        IFACEMETHODIMP UnlockStore() override { return m_attributes->UnlockStore(); }
        IFACEMETHODIMP GetCount(UINT32* count) override { return m_attributes->GetCount(count); }
        IFACEMETHODIMP GetItemByIndex(UINT32 index, GUID* key, PROPVARIANT* item) override { return m_attributes->GetItemByIndex(index, key, item); }
        IFACEMETHODIMP CopyAllItems(IMFAttributes* destination) override { return m_attributes->CopyAllItems(destination); }

    private:
        HMODULE m_module = nullptr;
        ComPtr<IMFAttributes> m_attributes;
        ComPtr<MediaSource> m_source;
    };
}
