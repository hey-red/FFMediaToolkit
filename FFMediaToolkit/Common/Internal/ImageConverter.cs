namespace FFMediaToolkit.Common.Internal
{
    using System;
    using System.Drawing;
    using FFMediaToolkit.Graphics;
    using FFmpeg.AutoGen;

    /// <summary>
    ///     A class used to convert ffmpeg <see cref="AVFrame" />s to <see cref="ImageData" /> objects with specified image
    ///     size and color format.
    /// </summary>
    internal unsafe class ImageConverter : Wrapper<SwsContext>
    {
        // sws_scale requires up to 16 extra bytes allocated in the input buffer when resizing an image
        // (reference: https://www.ffmpeg.org/doxygen/6.0/frame_8h_source.html#l00340)
        private const int BufferPaddingSize = 16;
        private readonly AVPixelFormat destinationFormat;

        private readonly Size destinationSize;
        private readonly bool flipVertically;
        private AVPixelFormat lastSourcePixelFormat;

        private Size lastSourceSize;
        private byte[] tmpBuffer = { };

        // Reusable scratch AVFrame used by AVFrameToBitmap. Its size and pixel format are always
        // destinationSize/destinationFormat, which never change during the lifetime of the converter, so it is
        // allocated once on first use instead of on every decoded frame.
        // Freed in OnDisposing.
        private AVFrame* scaledFrame;

        // Reusable argument arrays for sws_scale in FillAVFrame. They used to be created with `new` for every
        // encoded frame, which produced garbage on the encoding hot path.
        // Only element [0] is ever set.
        // Elements 1..3 stay null/0. The converter already keeps mutable state (sws context, tmpBuffer),
        // so it is not thread-safe anyway and sharing these arrays does not change that.
        private readonly byte*[] srcData = new byte*[4];
        private readonly int[] srcLinesize = new int[4];

        /// <summary>
        ///     Initializes a new instance of the <see cref="ImageConverter" /> class.
        /// </summary>
        /// <param name="destinationSize">Destination image size.</param>
        /// <param name="destinationFormat">Destination image format.</param>
        /// <param name="flipVertically">If <see langword="true" />, video frames will be flipped vertically.</param>
        public ImageConverter(Size destinationSize, AVPixelFormat destinationFormat, bool flipVertically)
            : base(null)
        {
            this.destinationSize = destinationSize;
            this.destinationFormat = destinationFormat;
            this.flipVertically = flipVertically;
        }

        /// <summary>
        ///     Overrides the <paramref name="destinationFrame" /> image buffer with the converted <see cref="ImageData" /> bitmap.
        ///     Used in encoding.
        /// </summary>
        /// <param name="bitmap">The input bitmap.</param>
        /// <param name="destinationFrame">The <see cref="AVFrame" /> to override.</param>
        internal void FillAVFrame(ImageData bitmap, VideoFrame destinationFrame)
        {
            UpdateContext(bitmap.ImageSize, (AVPixelFormat)bitmap.PixelFormat);

            var requiredBufferLength = bitmap.Stride * bitmap.ImageSize.Height + BufferPaddingSize;
            var shouldUseTmpBuffer = bitmap.ImageSize != destinationSize && bitmap.Data.Length < requiredBufferLength;

            if (shouldUseTmpBuffer)
            {
                if (tmpBuffer.Length < requiredBufferLength) tmpBuffer = new byte[requiredBufferLength];

                bitmap.Data.CopyTo(tmpBuffer);
            }

            var source = shouldUseTmpBuffer ? tmpBuffer : bitmap.Data;
            fixed (byte* ptr = source)
            {
                // Fill the reusable srcData/srcLinesize arrays instead of allocating new ones per frame.
                srcData[0] = flipVertically ? ptr + bitmap.Stride * (bitmap.ImageSize.Height - 1) : ptr;
                srcLinesize[0] = flipVertically ? -bitmap.Stride : bitmap.Stride;

                ffmpeg.sws_scale(Pointer, srcData, srcLinesize, 0, bitmap.ImageSize.Height, destinationFrame.Pointer->data,
                    destinationFrame.Pointer->linesize);

                // Do not keep a pointer into memory that is no longer pinned once we leave the fixed block.
                srcData[0] = null;
            }
        }

        /// <summary>
        ///     Converts a video <see cref="AVFrame" /> to the specified <see cref="ImageData" /> bitmap. Used in decoding.
        /// </summary>
        /// <param name="videoFrame">The video frame to convert.</param>
        /// <param name="destination">The destination <see cref="ImageData" />.</param>
        /// <param name="stride">Size of the single bitmap row.</param>
        internal void AVFrameToBitmap(VideoFrame videoFrame, byte* destination, int stride)
        {
            UpdateContext(videoFrame.Layout, videoFrame.PixelFormat);

            var srcHeight = videoFrame.Layout.Height;
            var dstWidth = destinationSize.Width;
            var dstHeight = destinationSize.Height;
            var outputFormat = destinationFormat;

            // Use a scratch AVFrame to hold the scaled output.
            // We do NOT write directly into the caller-owned destination buffer because
            // libswscale expects a proper AVFrame-backed allocation and the destination
            // buffer may have padding / stride / size differences.
            // The scratch frame is allocated once and reused for every call (see GetScaledFrame)
            // instead of being allocated and freed for each decoded frame.
            var tmp = GetScaledFrame();

            // Convert the source frame into the temporary AVFrame.
            // This is safe even when the source and destination dimensions differ.
            ffmpeg.sws_scale(
                Pointer,
                videoFrame.Pointer->data,
                videoFrame.Pointer->linesize,
                0,
                srcHeight,
                tmp->data,
                tmp->linesize);

            // Compute the real bytes-per-pixel for the target output format.
            var bytesPerPixel = GetBytesPerPixel(outputFormat);
            var rowLength = dstWidth * bytesPerPixel;
            var copyLength = Math.Min(rowLength, stride);

            // Fast path: when rows are not flipped, the scratch frame has exactly the same line size as the
            // destination and a row has no padding, both buffers are contiguous, so the whole image can be
            // copied with a single memcpy instead of one call per row.
            if (!flipVertically && tmp->linesize[0] == stride && copyLength == stride)
            {
                long totalBytes = (long)stride * dstHeight;
                Buffer.MemoryCopy(tmp->data[0], destination, totalBytes, totalBytes);
                return;
            }

            // Copy each scaled row into the caller-owned destination buffer.
            // This avoids stale right-edge pixels and works correctly with stride/padding.
            for (var y = 0; y < dstHeight; y++)
            {
                var srcY = flipVertically ? dstHeight - 1 - y : y;

                // Source row in the temporary frame.
                var src = tmp->data[0] + (srcY * tmp->linesize[0]);

                // Destination row in the caller buffer.
                var dst = destination + (y * stride);

                // Only copy the actual payload length; if stride is larger than the real row length,
                // the remaining bytes must be zeroed to avoid stale data.
                Buffer.MemoryCopy(src, dst, stride, copyLength);

                // Clear any padding bytes that remain after the real pixel payload.
                if (copyLength < stride)
                {
                    new Span<byte>(dst + copyLength, stride - copyLength).Clear();
                }
            }
        }

        private static int GetBytesPerPixel(AVPixelFormat format)
        {
            switch (format)
            {
                case AVPixelFormat.AV_PIX_FMT_BGR24:
                case AVPixelFormat.AV_PIX_FMT_RGB24:
                    return 3;
                case AVPixelFormat.AV_PIX_FMT_BGRA:
                case AVPixelFormat.AV_PIX_FMT_RGBA:
                case AVPixelFormat.AV_PIX_FMT_ARGB:
                    return 4;
                case AVPixelFormat.AV_PIX_FMT_BGR565LE:
                case AVPixelFormat.AV_PIX_FMT_BGR565BE:
                case AVPixelFormat.AV_PIX_FMT_BGR555LE:
                case AVPixelFormat.AV_PIX_FMT_BGR555BE:
                    return 2;
                default:
                    return 4;
            }
        }

        /// <inheritdoc />
        protected override void OnDisposing()
        {
            ffmpeg.sws_freeContext(Pointer);

            // Release the cached scratch frame allocated by GetScaledFrame (av_frame_free also frees its buffers).
            if (scaledFrame != null)
            {
                var frame = scaledFrame;
                ffmpeg.av_frame_free(&frame);
                scaledFrame = null;
            }
        }

        /// <summary>
        ///     Returns the reusable scratch <see cref="AVFrame" /> that receives the scaled output in
        ///     <see cref="AVFrameToBitmap" />, allocating it on first use. Its size and pixel format are always
        ///     the converter's destination size and format, which never change, so it never needs reallocation.
        /// </summary>
        /// <returns>The scratch frame. It is owned by this converter and freed in <see cref="OnDisposing" />.</returns>
        private AVFrame* GetScaledFrame()
        {
            if (scaledFrame != null)
            {
                return scaledFrame;
            }

            var frame = ffmpeg.av_frame_alloc();
            if (frame == null)
            {
                throw new FFmpegException("Cannot allocate AVFrame.");
            }

            frame->format = (int)destinationFormat;
            frame->width = destinationSize.Width;
            frame->height = destinationSize.Height;

            if (ffmpeg.av_frame_get_buffer(frame, 32) < 0)
            {
                ffmpeg.av_frame_free(&frame);
                throw new FFmpegException("av_frame_get_buffer failed.");
            }

            scaledFrame = frame;
            return frame;
        }

        private void UpdateContext(Size sourceSize, AVPixelFormat sourceFormat)
        {
            if (sourceSize != lastSourceSize || sourceFormat != lastSourcePixelFormat)
            {
                ffmpeg.sws_freeContext(Pointer);

                var scaleMode = sourceSize == destinationSize ? SwsFlags.SWS_POINT : SwsFlags.SWS_BICUBIC;
                var swsContext = ffmpeg.sws_getContext(sourceSize.Width, sourceSize.Height, sourceFormat,
                    destinationSize.Width, destinationSize.Height, destinationFormat, (int)scaleMode, null, null, null);

                if (swsContext == null)
                    throw new FFmpegException("Cannot allocate SwsContext.");

                UpdatePointer(swsContext);
                lastSourceSize = sourceSize;
                lastSourcePixelFormat = sourceFormat;
            }
        }
    }
}