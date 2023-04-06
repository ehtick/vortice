// Copyright © Amer Koleci.
// Licensed under the MIT License (MIT). See LICENSE in the repository root for more information.

using Win32;
using Win32.Graphics.Direct3D12;

namespace Vortice.Graphics.D3D12;

internal unsafe class D3D12Texture : Texture
{
    private readonly ComPtr<ID3D12Resource> _handle;

    public D3D12Texture(D3D12GraphicsDevice device, in TextureDescription description, void* initialData)
        : base(device, in description)
    {
        //var d3d12ResourceDesc = D3D12_RESOURCE_DESC.Tex2D(
        //      description.Size,
        //      D3D12_RESOURCE_FLAG_NONE
        //);
        //
        //var d3d12Device = device.Handle;
        //var d3d12ResourceAllocationInfo = d3d12Device->GetResourceAllocationInfo(visibleMask: 0, numResourceDescs: 1, &d3d12ResourceDesc);

        //d3d12Device->CreateCommittedResource()
    }

    public ID3D12Resource* Handle => _handle;

    protected override TextureView CreateView(in TextureViewDescription description) => throw new NotImplementedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
        }
    }

    protected override void OnLabelChanged(string newLabel)
    {
        fixed (char* pName = newLabel)
        {
            _ = Handle->SetName((ushort*)pName);
        }
    }
}
