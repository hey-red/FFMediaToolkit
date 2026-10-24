namespace FFMediaToolkit.Decoding.Internal
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;

    using FFmpeg.AutoGen;

    /// <summary>
    /// A stream wrapper.
    /// </summary>
    internal unsafe class AvioStream
    {
        // whence values of the AVIO seek callback (the same as stdio's SEEK_SET/SEEK_CUR/SEEK_END).
        private const int SeekSet = 0;
        private const int SeekCur = 1;
        private const int SeekEnd = 2;

        // AVSEEK_FORCE from libavformat/avio.h, defined here so the code does not depend on the binding's constant.
        private const int AvSeekForce = 0x20000;

        private readonly Stream inputStream;

#if !NETSTANDARD2_1
        // Intermediate managed buffer. Needed only on netstandard2.0, where Stream.Read(Span<byte>) does not exist.
        private byte[] readBuffer = null;
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="AvioStream"/> class.
        /// </summary>
        /// <param name="input">Multimedia file stream.</param>
        public AvioStream(Stream input)
        {
            inputStream = input ?? throw new ArgumentNullException(nameof(input));
        }

        /// <summary>
        /// A method for refilling the buffer. For stream protocols,
        /// must never return 0 but rather a proper AVERROR code.
        /// </summary>
        /// <param name="opaque">An opaque pointer.</param>
        /// <param name="buffer">A buffer that needs to be filled with stream data.</param>
        /// <param name="bufferLength">The size of <paramref name="buffer"/>.</param>
        /// <returns>Number of read bytes.</returns>
        public int Read(void* opaque, byte* buffer, int bufferLength)
        {
            // This method is called from native code (libavformat). An exception thrown by the managed Stream
            // (IOException, ObjectDisposedException, a network error, ...) cannot unwind through native frames and
            // would terminate the whole process, so it is converted to an AVERROR code that libavformat reports
            // as a normal I/O error.
            try
            {
#if NETSTANDARD2_1
                // Read straight into the native AVIO buffer.
                int readed = inputStream.Read(new Span<byte>(buffer, bufferLength));

                if (readed < 1)
                {
                    return ffmpeg.AVERROR_EOF;
                }

                return readed;
#else
                if (readBuffer == null || readBuffer.Length < bufferLength)
                {
                    readBuffer = new byte[bufferLength];
                }

                int readed = inputStream.Read(readBuffer, 0, bufferLength);

                if (readed < 1)
                {
                    return ffmpeg.AVERROR_EOF;
                }

                Marshal.Copy(readBuffer, 0, (IntPtr)buffer, readed);

                return readed;
#endif
            }
            catch (Exception)
            {
                return ffmpeg.AVERROR_EXTERNAL;
            }
        }

        /// <summary>
        /// A method for seeking to specified byte position.
        /// </summary>
        /// <param name="opaque">An opaque pointer.</param>
        /// <param name="offset">The offset in a stream.</param>
        /// <param name="whence">
        /// <c>SEEK_SET</c> (0), <c>SEEK_CUR</c> (1), <c>SEEK_END</c> (2) or <c>AVSEEK_SIZE</c>
        /// (return the stream size instead of seeking). <c>AVSEEK_FORCE</c> may be combined with any of them.
        /// </param>
        /// <returns>The new absolute position within the stream, the stream size for <c>AVSEEK_SIZE</c>, or a negative value on error.</returns>
        public long Seek(void* opaque, long offset, int whence)
        {
            if (!inputStream.CanSeek)
            {
                return -1;
            }

            // Like Read, this is called from native code, so an exception from the Stream must not escape.
            try
            {
                // AVSEEK_FORCE (0x20000 in libavformat/avio.h) only means "a slow seek is acceptable" and
                // can be OR-ed into whence.
                whence &= ~AvSeekForce;

                if (whence == ffmpeg.AVSEEK_SIZE)
                {
                    return inputStream.Length;
                }

                // The offset used to be treated as absolute for every whence value. The AVIO seek callback
                // contract is the same as lseek: SEEK_SET, SEEK_CUR and SEEK_END have to be honored. In practice
                // avio_seek() only forwards SEEK_SET, but avio_size() falls back to SEEK_END with offset -1
                // when the size is unknown, and custom callers may use the other values.
                if (whence == SeekSet)
                {
                    return inputStream.Seek(offset, SeekOrigin.Begin);
                }

                if (whence == SeekCur)
                {
                    return inputStream.Seek(offset, SeekOrigin.Current);
                }

                if (whence == SeekEnd)
                {
                    return inputStream.Seek(offset, SeekOrigin.End);
                }

                return -1;
            }
            catch (Exception)
            {
                return ffmpeg.AVERROR_EXTERNAL;
            }
        }
    }
}