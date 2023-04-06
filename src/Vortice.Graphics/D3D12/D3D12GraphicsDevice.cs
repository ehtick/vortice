// Copyright © Amer Koleci and Contributors.
// Licensed under the MIT License (MIT). See LICENSE in the repository root for more information.

using System.Diagnostics;
using CommunityToolkit.Diagnostics;
using Win32;
using Win32.Graphics.Direct3D;
using Win32.Graphics.Direct3D12;
using Win32.Graphics.Dxgi;
using static Win32.Apis;
using static Win32.Graphics.Dxgi.Apis;
using static Win32.Graphics.Direct3D12.Apis;
using DxgiInfoQueueFilter = Win32.Graphics.Dxgi.InfoQueueFilter;
using InfoQueueFilter = Win32.Graphics.Direct3D12.InfoQueueFilter;
using MessageId = Win32.Graphics.Direct3D12.MessageId;

namespace Vortice.Graphics.D3D12;

internal unsafe class D3D12GraphicsDevice : GraphicsDevice
{
    private static readonly Lazy<bool> s_isSupported = new(CheckIsSupported);

    public static bool IsSupported() => s_isSupported.Value;

    private readonly ComPtr<IDXGIFactory4> _dxgiFactory;
    private readonly ComPtr<ID3D12Device5> _handle;
    //private readonly ComPtr<D3D12MA_Allocator> _allocator;

    private readonly GraphicsAdapterInfo _adapterInfo;
    //private readonly GraphicsDeviceFeatures _features;
    private readonly GraphicsDeviceLimits _limits;
    private readonly FeatureLevel _featureLevel;

    public D3D12GraphicsDevice(in GraphicsDeviceDescription description)
        : base(GraphicsBackend.Direct3D12, description.Label)
    {
        Guard.IsTrue(IsSupported(), nameof(D3D12GraphicsDevice), "Direct3D12 is not supported");

        uint dxgiFactoryFlags = 0u;

        if (description.ValidationMode != ValidationMode.Disabled)
        {
            dxgiFactoryFlags = DXGI_CREATE_FACTORY_DEBUG;

            using ComPtr<ID3D12Debug> d3d12Debug = default;
            if (D3D12GetDebugInterface(__uuidof<ID3D12Debug>(), d3d12Debug.GetVoidAddressOf()).Success)
            {
                d3d12Debug.Get()->EnableDebugLayer();

                if (description.ValidationMode == ValidationMode.GPU)
                {
                    using ComPtr<ID3D12Debug1> d3d12Debug1 = default;
                    using ComPtr<ID3D12Debug2> d3d12Debug2 = default;

                    if (d3d12Debug.CopyTo(d3d12Debug1.GetAddressOf()).Success)
                    {
                        d3d12Debug1.Get()->SetEnableGPUBasedValidation(true);
                        d3d12Debug1.Get()->SetEnableSynchronizedCommandQueueValidation(true);
                    }

                    if (d3d12Debug.CopyTo(d3d12Debug2.GetAddressOf()).Success)
                    {
                        const bool g_D3D12DebugLayer_GPUBasedValidation_StateTracking_Enabled = true;

                        if (g_D3D12DebugLayer_GPUBasedValidation_StateTracking_Enabled)
                            d3d12Debug2.Get()->SetGPUBasedValidationFlags(GpuBasedValidationFlags.DisableStateTracking);
                        else
                            d3d12Debug2.Get()->SetGPUBasedValidationFlags(GpuBasedValidationFlags.None);
                    }
                }
            }
            else
            {
                Debug.WriteLine("WARNING: Direct3D Debug Device is not available");
            }

            // DRED
            using ComPtr<ID3D12DeviceRemovedExtendedDataSettings1> pDredSettings = default;
            if (D3D12GetDebugInterface(__uuidof<ID3D12DeviceRemovedExtendedDataSettings1>(), pDredSettings.GetVoidAddressOf()).Success)
            {
                // Turn on auto-breadcrumbs and page fault reporting.
                pDredSettings.Get()->SetAutoBreadcrumbsEnablement(DredEnablement.ForcedOn);
                pDredSettings.Get()->SetPageFaultEnablement(DredEnablement.ForcedOn);
                pDredSettings.Get()->SetBreadcrumbContextEnablement(DredEnablement.ForcedOn);
            }

#if DEBUG
            using ComPtr<IDXGIInfoQueue> dxgiInfoQueue = default;

            if (DXGIGetDebugInterface1(0u, __uuidof<IDXGIInfoQueue>(), dxgiInfoQueue.GetVoidAddressOf()).Success)
            {
                dxgiInfoQueue.Get()->SetBreakOnSeverity(DXGI_DEBUG_ALL, InfoQueueMessageSeverity.Error, true);
                dxgiInfoQueue.Get()->SetBreakOnSeverity(DXGI_DEBUG_ALL, InfoQueueMessageSeverity.Corruption, true);

                int* hide = stackalloc int[1]
                {
                    80 /* IDXGISwapChain::GetContainingOutput: The swapchain's adapter does not control the output on which the swapchain's window resides. */,
                };

                DxgiInfoQueueFilter filter = new()
                {
                    DenyList = new Win32.Graphics.Dxgi.InfoQueueFilterDescription()
                    {
                        NumIDs = 1,
                        pIDList = hide
                    }
                };

                dxgiInfoQueue.Get()->AddStorageFilterEntries(DXGI_DEBUG_DXGI, &filter);
            }
#endif
        }

        ThrowIfFailed(CreateDXGIFactory2(dxgiFactoryFlags, __uuidof<IDXGIFactory4>(), _dxgiFactory.GetVoidAddressOf()));

        // Determines whether tearing support is available for fullscreen borderless windows.
        {
            using ComPtr<IDXGIFactory5> dxgiFactory5 = default;
            HResult hr = _dxgiFactory.CopyTo(dxgiFactory5.GetAddressOf());

            if (hr.Success)
            {
                TearingSupported = dxgiFactory5.Get()->IsTearingSupported();
            }

            if (hr.Failure || !TearingSupported)
            {
#if DEBUG
                Debug.WriteLine("WARNING: Variable refresh rate displays not supported");
#endif
            }
        }

        {
            GpuPreference gpuPreference = description.PowerPreference.ToDxgi();

            using ComPtr<IDXGIFactory6> dxgiFactory6 = default;
            bool queryByPreference = _dxgiFactory.CopyTo(dxgiFactory6.GetAddressOf()).Success;

            using ComPtr<IDXGIAdapter1> dxgiAdapter = default;
            for (uint i = 0; NextAdapter(i, dxgiAdapter.ReleaseAndGetAddressOf()); ++i)
            {
                AdapterDescription1 adapterDesc;
                ThrowIfFailed(dxgiAdapter.Get()->GetDesc1(&adapterDesc));

                // Don't select the Basic Render Driver adapter.
                if ((adapterDesc.Flags & AdapterFlags.Software) != 0u)
                {
                    continue;
                }

                if (D3D12CreateDevice((IUnknown*)dxgiAdapter.Get(), FeatureLevel.Level_12_0,
                    __uuidof<ID3D12Device5>(), _handle.GetVoidAddressOf()).Success)
                {
                    break;
                }
            }

            // Create the DX12 API device object.
            Handle->SetName("AlimerDevice");

            if (description.ValidationMode != ValidationMode.Disabled)
            {
                //ID3D12DebugDevice1* debugDevice;
                //if (SUCCEEDED(d3dDevice->QueryInterface(&debugDevice)))
                //{
                //    const bool g_D3D12DebugLayer_AllowBehaviorChangingDebugAids = true;
                //    const bool g_D3D12DebugLayer_ConservativeResourceStateTracking = true;
                //    const bool g_D3D12DebugLayer_DisableVirtualizedBundlesValidation = false;
                //
                //    uint32_t featureFlags = 0;
                //    if (g_D3D12DebugLayer_AllowBehaviorChangingDebugAids)
                //        featureFlags |= D3D12_DEBUG_FEATURE_ALLOW_BEHAVIOR_CHANGING_DEBUG_AIDS;
                //    if (g_D3D12DebugLayer_ConservativeResourceStateTracking)
                //        featureFlags |= D3D12_DEBUG_FEATURE_CONSERVATIVE_RESOURCE_STATE_TRACKING;
                //    if (g_D3D12DebugLayer_DisableVirtualizedBundlesValidation)
                //        featureFlags |= D3D12_DEBUG_FEATURE_DISABLE_VIRTUALIZED_BUNDLES_VALIDATION;
                //
                //    ThrowIfFailed(debugDevice->SetDebugParameter(D3D12_DEBUG_DEVICE_PARAMETER_FEATURE_FLAGS, &featureFlags, sizeof featureFlags));
                //    debugDevice->Release();
                //}

                // Configure debug device (if active).
                using ComPtr<ID3D12InfoQueue> infoQueue = default;
                if (_handle.CopyTo(infoQueue.GetAddressOf()).Success)
                {
                    infoQueue.Get()->SetBreakOnSeverity(MessageSeverity.Corruption, true);
                    infoQueue.Get()->SetBreakOnSeverity(MessageSeverity.Error, true);

                    // These severities should be seen all the time
                    uint enabledSeveritiesCount = (description.ValidationMode == ValidationMode.Verbose) ? 5u : 4u;
                    MessageSeverity* enabledSeverities = stackalloc MessageSeverity[5]
                    {
                        MessageSeverity.Corruption,
                        MessageSeverity.Error,
                        MessageSeverity.Warning,
                        MessageSeverity.Message,
                        MessageSeverity.Info
                    };

                    const int disabledMessagesCount = 9;
                    MessageId* disabledMessages = stackalloc MessageId[disabledMessagesCount]
                    {
                        MessageId.ClearRenderTargetViewMismatchingClearValue,
                        MessageId.ClearDepthStencilViewMismatchingClearValue,
                        MessageId.MapInvalidNullRange,
                        MessageId.UnmapInvalidNullRange,
                        MessageId.ExecuteCommandListsWrongSwapchainBufferReference,
                        MessageId.ResourceBarrierMismatchingCommandListType,
                        MessageId.ExecuteCommandListsGpuWrittenReadbackResourceMapped,
                        MessageId.LoadpipelineNamenotfound,
                        MessageId.StorepipelineDuplicatename
                    };

                    InfoQueueFilter filter = new();
                    filter.AllowList.NumSeverities = enabledSeveritiesCount;
                    filter.AllowList.pSeverityList = enabledSeverities;
                    filter.DenyList.NumIDs = disabledMessagesCount;
                    filter.DenyList.pIDList = disabledMessages;

                    // Clear out the existing filters since we're taking full control of them
                    _ = infoQueue.Get()->PushEmptyStorageFilter();

                    ThrowIfFailed(infoQueue.Get()->AddStorageFilterEntries(&filter));
                }
            }

            // Create allocator
            //D3D12MA_ALLOCATOR_DESC allocatorDesc = default;
            //allocatorDesc.pDevice = (ID3D12Device*)Handle;
            //allocatorDesc.pAdapter = (IDXGIAdapter*)dxgiAdapter.Get();
            //ThrowIfFailed(D3D12MemAlloc.D3D12MA_CreateAllocator(&allocatorDesc, _allocator.GetAddressOf()));

            bool NextAdapter(uint index, IDXGIAdapter1** ppAdapter)
            {
                if (queryByPreference)
                    return dxgiFactory6.Get()->EnumAdapterByGpuPreference(index, gpuPreference, __uuidof<IDXGIAdapter1>(), (void**)ppAdapter) != DXGI_ERROR_NOT_FOUND;
                else
                    return _dxgiFactory.Get()->EnumAdapters1(index, ppAdapter) != DXGI_ERROR_NOT_FOUND;
            }
        }
    }

    /// <inheritdoc />
    public override GraphicsAdapterInfo AdapterInfo => _adapterInfo;

    /// <inheritdoc />
    //public override GraphicsDeviceFeatures Features => _features;

    /// <inheritdoc />
    public override GraphicsDeviceLimits Limits => _limits;

    public IDXGIFactory4* DXGIFactory => _dxgiFactory;
    public ID3D12Device5* Handle => _handle;

    public bool TearingSupported { get; }

    /// <summary>
    /// Finalizes an instance of the <see cref="D3D12GraphicsDevice" /> class.
    /// </summary>
    ~D3D12GraphicsDevice() => Dispose(disposing: false);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            //_allocator.Dispose();

#if DEBUG
            uint refCount = _handle.Get()->Release();
            if (refCount > 0)
            {
                Debug.WriteLine($"Direct3D11: There are {refCount} unreleased references left on the device");

                //ID3D11Debug? d3d11Debug = NativeDevice.QueryInterfaceOrNull<ID3D11Debug>();
                //if (d3d11Debug != null)
                //{
                //    d3d11Debug.ReportLiveDeviceObjects(ReportLiveDeviceObjectFlags.Detail | ReportLiveDeviceObjectFlags.IgnoreInternal);
                //    d3d11Debug.Dispose();
                //}
            }
#else
            _handle.Dispose();
#endif

            _dxgiFactory.Dispose();

#if DEBUG
            using ComPtr<IDXGIDebug1> dxgiDebug = default;
            if (DXGIGetDebugInterface1(0u, __uuidof<IDXGIDebug1>(), dxgiDebug.GetVoidAddressOf()).Success)
            {
                dxgiDebug.Get()->ReportLiveObjects(DXGI_DEBUG_ALL, ReportLiveObjectFlags.Summary | ReportLiveObjectFlags.IgnoreInternal);
            }
#endif
        }
    }

    /// <inheritdoc />
    public override void WaitIdle()
    {
        //ImmediateContext.Flush();
    }

    public override bool QueryFeature(Feature feature) => throw new NotImplementedException();
    public override CommandBuffer BeginCommandBuffer(string? label = null) => throw new NotImplementedException();
    protected override GraphicsBuffer CreateBufferCore(in BufferDescription description, void* initialData) => new D3D12GraphicsBuffer(this, in description, initialData);
    protected override Texture CreateTextureCore(in TextureDescription description, void* initialData) => new D3D12Texture(this, in description, initialData);
    protected override void SubmitCommandBuffers(CommandBuffer[] commandBuffers, int count) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override SwapChain CreateSwapChainCore(SwapChainSurface surface, in SwapChainDescription description)
    {
        throw new NotImplementedException();
    }

    private static bool CheckIsSupported()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                return false;
            }

            using ComPtr<IDXGIFactory4> dxgiFactory = default;

            if (CreateDXGIFactory1(__uuidof<IDXGIFactory2>(), dxgiFactory.GetVoidAddressOf()).Failure)
            {
                return false;
            }

            using ComPtr<IDXGIAdapter1> dxgiAdapter = default;
            bool foundCompatibleDevice = false;
            for (uint adapterIndex = 0;
                dxgiFactory.Get()->EnumAdapters1(adapterIndex, dxgiAdapter.ReleaseAndGetAddressOf()).Success;
                adapterIndex++)
            {
                AdapterDescription1 adapterDesc;
                ThrowIfFailed(dxgiAdapter.Get()->GetDesc1(&adapterDesc));

                if ((adapterDesc.Flags & AdapterFlags.Software) != 0u)
                {
                    // Don't select the Basic Render Driver adapter.
                    continue;
                }

                // Check to see if the adapter supports Direct3D 12, but don't create the actual device.
                if (D3D12CreateDevice((IUnknown*)dxgiAdapter.Get(), FeatureLevel.Level_12_0,
                    __uuidof<ID3D12Device>(), null).Success)
                {
                    foundCompatibleDevice = true;
                    break;
                }
            }

            if (!foundCompatibleDevice)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

}
