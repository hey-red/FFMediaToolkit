﻿namespace FFMediaToolkit.Encoding.Internal
{
    using System;
    using FFMediaToolkit.Common;
    using FFMediaToolkit.Common.Internal;
    using FFMediaToolkit.Helpers;
    using FFmpeg.AutoGen;

    /// <summary>
    /// Contains method for creating media streams.
    /// </summary>
    internal static unsafe class EncoderFactory
    {
        /// <summary>
        /// Creates a new video stream for the specified <see cref="OutputContainer"/>.
        /// </summary>
        /// <param name="container">The media container.</param>
        /// <param name="config">The stream settings.</param>
        /// <returns>The new video stream.</returns>
        public static Encoder<VideoFrame> CreateVideo(OutputContainer container, VideoEncoderSettings config)
        {
            AVCodec* codec;
            if (!string.IsNullOrEmpty(config.CodecName))
            {
                codec = ffmpeg.avcodec_find_encoder_by_name(config.CodecName);
            }
            else
            {
                var codecId = config.Codec == VideoCodec.Default ? container.Pointer->oformat->video_codec : (AVCodecID)config.Codec;
                if (codecId == AVCodecID.AV_CODEC_ID_NONE)
                    throw new InvalidOperationException("The media container doesn't support video!");

                codec = ffmpeg.avcodec_find_encoder(codecId);
            }

            if (codec == null)
                throw new InvalidOperationException($"Cannot find the requested video codec");

            if (codec->type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                throw new InvalidOperationException($"The {codec->id} encoder doesn't support video!");

            var stream = ffmpeg.avformat_new_stream(container.Pointer, codec);
            if (stream == null)
                throw new InvalidOperationException("Cannot allocate AVStream");

            stream->time_base = config.TimeBase;
            stream->avg_frame_rate = config.FramerateRational;

            var codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
                throw new InvalidOperationException("Cannot allocate AVCodecContext");

            codecContext->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            codecContext->codec_id = codec->id;
            codecContext->width = config.VideoWidth;
            codecContext->height = config.VideoHeight;
            codecContext->pix_fmt = (AVPixelFormat)config.VideoFormat;
            codecContext->bit_rate = config.Bitrate;
            codecContext->framerate = config.FramerateRational;
            codecContext->time_base = stream->time_base;
            codecContext->gop_size = config.KeyframeRate;

            var dict = new FFDictionary(config.CodecOptions);

            if (config.CRF.HasValue && config.Codec.IsMatch(VideoCodec.H264, VideoCodec.H265, VideoCodec.VP9, VideoCodec.VP8))
            {
                dict["crf"] = config.CRF.Value.ToString();
            }

            if (config.QP.HasValue && config.Codec.IsMatch(VideoCodec.H266))
            {
                dict["qp"] = config.QP.Value.ToString();
            }

            if (config.Codec.IsMatch(VideoCodec.H264, VideoCodec.H265, VideoCodec.H266))
            {
                dict["preset"] = config.EncoderPreset.GetDescription();
            }

            if ((container.Pointer->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            var ptr = dict.Pointer;
            ffmpeg.avcodec_open2(codecContext, codec, &ptr).ThrowIfError("Failed to open video encoder.");
            dict.Update(ptr);

            ffmpeg.avcodec_parameters_from_context(stream->codecpar, codecContext).ThrowIfError("Cannot copy encoder parameters to output stream");

            return new Encoder<VideoFrame>(stream, codecContext, container);
        }

        /// <summary>
        /// Creates a new audio stream for the specified <see cref="OutputContainer"/>.
        /// </summary>
        /// <param name="container">The media container.</param>
        /// <param name="config">The stream settings.</param>
        /// <returns>The new audio stream.</returns>
        public static Encoder<AudioFrame> CreateAudio(OutputContainer container, AudioEncoderSettings config)
        {
            AVCodec* codec;
            if (!string.IsNullOrEmpty(config.CodecName))
            {
                codec = ffmpeg.avcodec_find_encoder_by_name(config.CodecName);
            }
            else
            {
                var codecId = config.Codec == AudioCodec.Default ? container.Pointer->oformat->audio_codec : (AVCodecID)config.Codec;
                if (codecId == AVCodecID.AV_CODEC_ID_NONE)
                    throw new InvalidOperationException("The media container doesn't support audio!");

                codec = ffmpeg.avcodec_find_encoder(codecId);
            }

            if (codec == null)
                throw new InvalidOperationException($"Cannot find the requested audio codec");

            if (codec->type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                throw new InvalidOperationException($"The {codec->id} encoder doesn't support audio!");

            var stream = ffmpeg.avformat_new_stream(container.Pointer, codec);
            if (stream == null)
                throw new InvalidOperationException("Cannot allocate AVStream");

            var codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
                throw new InvalidOperationException("Cannot allocate AVCodecContext");

            stream->codecpar->codec_id = codec->id;
            stream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_AUDIO;
            stream->codecpar->sample_rate = config.SampleRate;
            stream->codecpar->frame_size = config.SamplesPerFrame;
            stream->codecpar->format = (int)config.SampleFormat;

            ffmpeg.av_channel_layout_default(&stream->codecpar->ch_layout, config.Channels);
            stream->codecpar->bit_rate = config.Bitrate;

            ffmpeg.avcodec_parameters_to_context(codecContext, stream->codecpar).ThrowIfError("Cannot copy stream parameters to encoder");
            codecContext->time_base = config.TimeBase;

            if ((container.Pointer->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            var dict = new FFDictionary(config.CodecOptions);
            var ptr = dict.Pointer;

            ffmpeg.avcodec_open2(codecContext, codec, &ptr).ThrowIfError("Failed to open audio encoder.");

            dict.Update(ptr);

            ffmpeg.avcodec_parameters_from_context(stream->codecpar, codecContext).ThrowIfError("Cannot copy encoder parameters to output stream");

            return new Encoder<AudioFrame>(stream, codecContext, container);
        }
    }
}
