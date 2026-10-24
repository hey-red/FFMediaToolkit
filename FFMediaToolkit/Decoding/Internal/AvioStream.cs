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
#if NETSTANDARD2_1
            // Read straight into the native AVIO buffer.
            int readed = inputStream.Read(new Span<byte>(buffer, bufferLength));

            if (readed < 1)
            {
                return ffmpeg.AVERROR_EOF;
            }

            return readed;
#else
            // netstandard2.0 has no Stream.Read(Span<byte>), so the intermediate managed buffer is kept here.
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

        /// <summary>
        /// A method for seeking to specified byte position.
        /// </summary>
        /// <param name="opaque">An opaque pointer.</param>
        /// <param name="offset">The offset in a stream.</param>
        /// <param name="whence">The seek option.</param>
        /// <returns>Position within the current stream or stream size.</returns>
        public long Seek(void* opaque, long offset, int whence)
        {
            if (!inputStream.CanSeek)
            {
                return -1;
            }

            return whence == ffmpeg.AVSEEK_SIZE ?
                inputStream.Length :
                inputStream.Seek(offset, SeekOrigin.Begin);
        }
    }
}