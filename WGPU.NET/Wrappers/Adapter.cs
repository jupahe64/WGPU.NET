﻿using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static WGPU.NET.Wgpu;

namespace WGPU.NET
{
    public struct RequiredLimits
    {
        public Limits Limits;
    }

    public partial struct RequiredLimitsExtras
    {
        public uint MaxPushConstantSize;
    }

    public struct DeviceExtras
    {
        public string TracePath;
    }

    public class Adapter
    {
        internal AdapterImpl Impl;

        internal Adapter(AdapterImpl impl)
        {
            if (impl.Handle == IntPtr.Zero)
                throw new ResourceCreationError(nameof(Adapter));

            Impl = impl;
        }


        public unsafe FeatureName[] EnumerateFeatures()
        {
            FeatureName features = default;

            ulong size = AdapterEnumerateFeatures(Impl, ref features);

            var featuresSpan = new Span<FeatureName>(Unsafe.AsPointer(ref features), (int)size);

            FeatureName[] result = new FeatureName[size];

            featuresSpan.CopyTo(result);

            return result;
        }

        public bool GetLimits(out SupportedLimits limits)
        {
            limits = new SupportedLimits();

            return AdapterGetLimits(Impl, ref limits);
        }

        public void GetProperties(out AdapterProperties properties)
        {
            properties = new AdapterProperties();

            AdapterGetProperties(Impl, ref properties);
        }

        public bool HasFeature(FeatureName feature) => AdapterHasFeature(Impl, feature);

        public void RequestDevice(RequestDeviceCallback callback, string label, NativeFeature[] nativeFeatures, QueueDescriptor defaultQueue = default, 
            Limits? limits = null, RequiredLimitsExtras? limitsExtras = null, DeviceExtras? deviceExtras = null)
        {
            AdapterRequestDevice(Impl, new DeviceDescriptor()
            {
                defaultQueue = defaultQueue,
                requiredLimits = limits==null ? IntPtr.Zero : 
                Util.AllocHStruct(new Wgpu.RequiredLimits
                {
                    nextInChain = limitsExtras == null ? IntPtr.Zero : 
                    new WgpuStructChain()
                    .AddRequiredLimitsExtras(
                        limitsExtras.Value.MaxPushConstantSize)
                    .GetPointer(),
                    limits = limits.Value
                })
                ,
                requiredFeaturesCount = (uint)nativeFeatures.Length,
                requiredFeatures = Util.AllocHArray(nativeFeatures),
                label = label,
                nextInChain = deviceExtras==null ? IntPtr.Zero :
                new WgpuStructChain()
                .AddDeviceExtras(
                    deviceExtras.Value.TracePath)
                .GetPointer()
            }, 
            (s,d,m,_) => callback(s,new Device(d),m), IntPtr.Zero);
        }
    }

    public delegate void RequestDeviceCallback(RequestDeviceStatus status, Device device, string message);
}
