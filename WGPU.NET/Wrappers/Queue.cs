using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static WGPU.NET.Wgpu;

namespace WGPU.NET
{
    public class Queue
    {
        private static Dictionary<QueueImpl, Queue> instances =
            new Dictionary<QueueImpl, Queue>();

        internal QueueImpl Impl;

        private Queue(QueueImpl impl)
        {
            if (impl.Handle == IntPtr.Zero)
                throw new ResourceCreationError(nameof(Queue));

            Impl = impl;
        }

        internal static Queue For(QueueImpl impl)
            => impl.Handle == IntPtr.Zero ? null : instances.GetOrCreate(impl, () => new Queue(impl));

        public void OnSubmittedWorkDone(QueueWorkDoneCallback callback)
        {
            QueueOnSubmittedWorkDone(Impl,
                (s, d) => callback(s), 
                IntPtr.Zero
            );
        }

        public unsafe void Submit(CommandBuffer[] commands)
        {
            QueueSubmit(Impl, (uint)commands.Length,
                ref Unsafe.AsRef<CommandBufferImpl>(
                    (void*)Util.AllocHArray(commands.Length, commands.Select(x=>x.Impl))
                )
            );
        }

        /// <summary>
        /// Remarks: total size in bytes of <paramref name="data"/> has to be aligned to 16 byte, 
        /// use <see cref="WriteBufferAligned{T}(Buffer, ulong, ReadOnlySpan{T})"/> if unsure
        /// </summary>
        public unsafe void WriteBuffer<T>(Buffer buffer, ulong bufferOffset, ReadOnlySpan<T> data)
            where T : unmanaged
        {
            ulong structSize = (ulong)sizeof(T);


            QueueWriteBuffer(Impl, buffer.Impl, bufferOffset,
                (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(data)), 
                (ulong)data.Length * structSize);
        }

        /// <summary>
        /// Will make sure total size-in-bytes of the data written is aligned to 16 byte 
        /// <br>
        /// by reading beyond the <paramref name="data"/>-span and 
        /// writing those additional bytes to the <paramref name="buffer"/> if not aligned
        /// </br>
        /// </summary>
        public unsafe void WriteBufferAligned<T>(Buffer buffer, ulong bufferOffset, ReadOnlySpan<T> data)
            where T : unmanaged
        {
            ulong structSize = (ulong)sizeof(T);

            var dataSize = (ulong)data.Length * structSize;

            QueueWriteBuffer(Impl, buffer.Impl, bufferOffset,
                (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(data)),
                (dataSize + 15) / 16 * 16);
        }

        public unsafe void WriteTexture<T>(ImageCopyTexture destination, ReadOnlySpan<T> data, 
            in TextureDataLayout dataLayout, in Extent3D writeSize)
            where T : unmanaged
        {
            ulong structSize = (ulong)Marshal.SizeOf<T>();


            QueueWriteTexture(Impl, destination,
                (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(data)),
                (ulong)data.Length * structSize,
                dataLayout, in writeSize);
        }
    }
}
