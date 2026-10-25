// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

// Order matters: ole2.h (which brings cguid.h) must come before ks.h, because ks.h redefines
// GUID_NULL as a macro. Same order as Microsoft's VirtualCamera sample.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <unknwn.h>
#include <windows.h>
#include <propvarutil.h>
#include <sddl.h>
#include <ole2.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <initguid.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <mferror.h>
#include <mfvirtualcamera.h>

#include <atomic>
#include <mutex>
#include <string>
#include <vector>

#include "VirtualCameraProtocol.h"

using Microsoft::WRL::ComPtr;

#define LK_RETURN_IF_FAILED(expr) do { const HRESULT _hr = (expr); if (FAILED(_hr)) { return _hr; } } while (false)
