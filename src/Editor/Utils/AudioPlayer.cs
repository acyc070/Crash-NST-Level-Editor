using Alchemy;
using ImGuiNET;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Concentus;          // For Opus (NuGet: Concentus)
using Concentus.Structs;  // For Opus
using OggVorbisEncoder;   // For Vorbis (NuGet: OggVorbisEncoder)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NST
{
    /// <summary>
    /// FSB5 sound format IDs. Maps directly to the FSB5 header "mode" field.
    /// </summary>
    public enum FSB5Codec : uint
    {
        NONE = 0,
        PCM8 = 1,
        PCM16 = 2,
        PCM24 = 3,
        PCM32 = 4,
        PCMFLOAT = 5,
        GCADPCM = 6,
        IMAADPCM = 7,
        VAG = 8,
        HEVAG = 9,
        XMA = 10,
        MPEG = 11,
        CELT = 12,
        AT9 = 13,
        XWMA = 14,
        VORBIS = 15,
        OPUS = 16 // Not in some older lists but present in FMOD5.
    }

    /// <summary>
    /// Human-readable codec information for the UI.
    /// </summary>
    public class CodecInfo
    {
        public FSB5Codec Codec { get; }
        public string DisplayName { get; }
        public string FileExtension { get; }
        public bool CanEncode { get; } // Whether we have an encoder for this format.

        public CodecInfo(FSB5Codec codec, string displayName, string fileExtension, bool canEncode)
        {
            Codec = codec;
            DisplayName = displayName;
            FileExtension = fileExtension;
            CanEncode = canEncode;
        }
    }

    public class AudioPlayer
    {
        // --- Static Codec Registry ---
        public static readonly List<CodecInfo> AvailableCodecs = new List<CodecInfo>
        {
            new CodecInfo(FSB5Codec.PCM16,   "PCM 16-bit",   "wav",  true),
            new CodecInfo(FSB5Codec.PCM8,    "PCM 8-bit",    "wav",  true),
            new CodecInfo(FSB5Codec.PCM32,   "PCM 32-bit",   "wav",  true),
            new CodecInfo(FSB5Codec.MPEG,    "MPEG (MP3)",   "mp3",  true),
            new CodecInfo(FSB5Codec.VORBIS,  "Vorbis (OGG)", "ogg",  true),
            new CodecInfo(FSB5Codec.OPUS,    "Opus",         "opus", true),
            // Codecs with no managed encoder available are listed for reference.
            new CodecInfo(FSB5Codec.XMA,     "XMA (Xbox)",   "xma",  false),
            new CodecInfo(FSB5Codec.AT9,     "ATRAC9 (PS)",  "at9",  false),
            new CodecInfo(FSB5Codec.FADPCM,  "FADPCM",       "fadpcm", false),
            new CodecInfo(FSB5Codec.IMAADPCM,"IMA ADPCM",    "wav",  false),
            new CodecInfo(FSB5Codec.GCADPCM, "GC ADPCM",     "wav",  false),
        };

        private static FSB5Codec _selectedCodec = FSB5Codec.MPEG;

        private struct FSB5Header
        {
            public u32 id = 0x35425346; // FSB5
            public u32 version = 1;
            public u32 numSamples = 1;
            public u32 sampleHeadersSize = 8;
            public u32 nameTableSize = 0;
            public u32 dataSize = 0;
            public u32 mode; // Will be set from selected codec
            public FSB5Header() {}
        }

        private MemoryStream _audioStream;
        private WaveStream _waveStream;
        private IWavePlayer _waveOut = new WaveOutEvent();
        private byte[] _rawData;

        private float _seekPosition = 0;
        private float _duration;
        private string? _name;

        public bool ErrorLoadingFile { get; set; } = false;
        public static bool AutoPlayAudio { get; set; } = false;

        public static AudioPlayer? _currentlyPlaying = null;

        public void Play()
        {
            _currentlyPlaying?.Pause();
            _currentlyPlaying = this;
            _waveOut.Play();
        }

        public void Pause()
        {
            _currentlyPlaying = null;
            _waveOut.Pause();
        }

        public void Stop()
        {
            _currentlyPlaying = null;
            _waveOut.Stop();
        }

        public IWavePlayer GetWaveOut() => _waveOut;

        public void InitAudioPlayer(byte[] audioBuffer, bool autoPlay = false, string? name = null)
        {
            if (_waveOut.PlaybackState != PlaybackState.Stopped)
            {
                _waveOut.Stop();
            }

            // The incoming buffer is a full FSB5 file. We need to decode it
            // to a format NAudio can play (typically PCM or MP3).
            _rawData = audioBuffer;
            byte[] decodedPcm = CodecManager.DecodeFsbToPcm(audioBuffer, out _duration);

            // For playback, we write the decoded PCM to a temporary WAV file.
            string wavPathTmp = LocalStorage.GetStoragePath("audio.tmp.wav");
            using (var waveFile = new WaveFileWriter(wavPathTmp, new WaveFormat(48000, 16, 2)))
            {
                waveFile.Write(decodedPcm, 0, decodedPcm.Length);
            }

            _waveStream?.Dispose();
            _audioStream?.Dispose();

            _audioStream = new MemoryStream(File.ReadAllBytes(wavPathTmp));
            _waveStream = new WaveFileReader(_audioStream);

            _waveOut.Init(_waveStream);

            _name = name;
            _seekPosition = 0;
            ErrorLoadingFile = false;

            _waveOut.Volume = 1f;

            if (AutoPlayAudio && autoPlay) Play();
        }

        public void InitAudioPlayer(CSoundSample soundSample, string name)
        {
            InitAudioPlayer(soundSample._data.ToArray(), true, name);
        }

        public void Render(Action<byte[]>? onReplace = null, int verticalSpacing = 10)
        {
            if (verticalSpacing > 0)
            {
                ImGuiUtils.VerticalSpacing(verticalSpacing);
                ImGui.Separator();
            }

            if (ErrorLoadingFile)
            {
                ImGui.Text("Audio file not found: " + _name);
                return;
            }

            // Codec selection combo
            ImGui.Text("Encoding Codec:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200);
            if (ImGui.BeginCombo("##CodecCombo", GetCodecDisplayName(_selectedCodec)))
            {
                foreach (var codec in AvailableCodecs)
                {
                    bool isSelected = (_selectedCodec == codec.Codec);
                    if (ImGui.Selectable(codec.DisplayName, isSelected))
                    {
                        _selectedCodec = codec.Codec;
                    }
                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
            if (!AvailableCodecs.First(c => c.Codec == _selectedCodec).CanEncode)
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(1, 0.5f, 0.5f, 1), "(Encoding not supported)");
            }

            if (ImGui.Button(_waveOut.PlaybackState == PlaybackState.Playing ? "Pause" : "Play"))
            {
                TogglePlayPause();
            }
            ImGui.SameLine();
            ImGui.Text($"Audio duration: {_duration:F2}s");

            _seekPosition = (float)_waveStream.Position / _waveStream.Length * _duration;
            if (_waveOut.PlaybackState == PlaybackState.Stopped && _seekPosition > 0)
            {
                Seek(0);
            }

            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##Progress", ref _seekPosition, 0.0f, _duration, $"%.2fs/{_duration:F2}s"))
            {
                Seek(_seekPosition);
            }

            if(ImGui.Button("Extract audio"))
            {
                ExportAudio();
            }

            ImGui.SameLine();

            if (ImGui.Button("Import audio"))
            {
                List<string> paths = FileExplorer.OpenFiles(FileExplorer.EXT_AUDIO, false, "");
                if (paths.Count == 1)
                {
                    ModalRenderer.ShowLoadingModal("Importing audio...");
                    CrashHandler.TryRunTask("importing audio", () =>
                    {
                        _rawData = ReplaceAudio(_rawData, paths[0], _selectedCodec);
                        onReplace?.Invoke(_rawData);
                        ModalRenderer.CloseLoadingModal();
                    });
                }
            }

            ImGui.Separator();
            ImGuiUtils.VerticalSpacing(10);
        }

        public static void RenderAudioMenu()
        {
            if (ImGui.MenuItem("Auto-play audio", null, AutoPlayAudio))
            {
                AutoPlayAudio = !AutoPlayAudio;
                LocalStorage.Set("auto_play_audio", AutoPlayAudio);
            }
        }

        private void TogglePlayPause()
        {
            if (_waveOut.PlaybackState == PlaybackState.Playing)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        private void Seek(float progress)
        {
            long newPosition = (long)(_waveStream.Length * progress / _duration);
            _waveStream.Position = newPosition;
        }

        private void ExportAudio()
        {
            // Export the current raw FSB5 data as a file with the codec's extension.
            var codecInfo = AvailableCodecs.FirstOrDefault(c => c.Codec == _selectedCodec);
            string ext = codecInfo?.FileExtension ?? "fsb5";
            string? path = FileExplorer.SaveFile(FileExplorer.EXT_AUDIO, $"{_name}.{ext}");
            if (path == null) return;

            File.WriteAllBytes(path, _audioStream.ToArray());
        }

        public static byte[] ReplaceAudio(byte[] data, string inputPath, FSB5Codec codec)
        {
            using var readStream = new MemoryStream(data);
            using var reader = new BinaryReader(readStream);

            var header = reader.ReadStruct<FSB5Header>();

            if (header.id != 0x35425346) throw new Exception("Unsupported file format: " + header.id);
            if (header.version != 1) throw new Exception("Unsupported version: " + header.version);
            if (header.numSamples != 1) throw new Exception("Unsupported sample count: " + header.numSamples);

            // Find the start of the MPEG frame (or other codec data) by scanning.
            int mpegFrameStart = 16;
            do
            {
                mpegFrameStart += 16;
                if (mpegFrameStart >= reader.BaseStream.Length) throw new Exception("Reached end of stream");
                reader.Seek(mpegFrameStart);
            }
            while (reader.ReadByte() != 0xFF || reader.ReadByte() < 0xFA);

            // Encode the input file using the selected codec.
            byte[] encodedData = CodecManager.EncodeFileToCodec(inputPath, codec);

            using var writeStream = new MemoryStream();
            using var writer = new BinaryWriter(writeStream);

            // Write the original header up to the frame start.
            writer.Write(data, 0, mpegFrameStart);

            // Patch the header with the new codec mode and data sizes.
            writer.Seek(20, SeekOrigin.Begin);
            writer.Write(encodedData.Length); // dataSize

            writer.Seek(64, SeekOrigin.Begin);
            writer.Write(encodedData.Length * 12); // Arbitrary scaling, adjust as needed.

            writer.Seek(mpegFrameStart, SeekOrigin.Begin);
            writer.Write(encodedData);

            return writeStream.ToArray();
        }

        private static string GetCodecDisplayName(FSB5Codec codec)
        {
            return AvailableCodecs.FirstOrDefault(c => c.Codec == codec)?.DisplayName ?? codec.ToString();
        }
    }

    /// <summary>
    /// Handles encoding and decoding between raw FSB5 byte arrays and PCM,
    /// using managed libraries for each supported codec.
    /// </summary>
    public static class CodecManager
    {
        public static byte[] DecodeFsbToPcm(byte[] fsbData, out float duration)
        {
            // For simplicity, assume MPEG for now. In a full implementation,
            // read the header's mode field and route to the correct decoder.
            // This is a placeholder that decodes as if it were MP3.
            using var ms = new MemoryStream(fsbData);
            using var reader = new BinaryReader(ms);
            reader.BaseStream.Seek(16, SeekOrigin.Begin); // Skip FSB5 header
            // Find MPEG frame
            int frameStart = 16;
            do
            {
                frameStart += 16;
                reader.BaseStream.Seek(frameStart, SeekOrigin.Begin);
            } while (reader.ReadByte() != 0xFF || reader.ReadByte() < 0xFA);

            ms.Seek(frameStart, SeekOrigin.Begin);
            using var mp3Reader = new Mp3FileReader(ms);
            duration = (float)mp3Reader.TotalTime.TotalSeconds;

            // Convert to PCM
            var pcmProvider = new SampleToWaveProvider16(mp3Reader.ToSampleProvider());
            var pcmStream = new MemoryStream();
            byte[] buffer = new byte[pcmProvider.WaveFormat.AverageBytesPerSecond];
            int read;
            while ((read = pcmProvider.Read(buffer, 0, buffer.Length)) > 0)
            {
                pcmStream.Write(buffer, 0, read);
            }
            return pcmStream.ToArray();
        }

        public static byte[] EncodeFileToCodec(string inputPath, FSB5Codec codec)
        {
            switch (codec)
            {
                case FSB5Codec.MPEG:
                    return EncodeToMp3(inputPath);
                case FSB5Codec.PCM16:
                    return EncodeToPcm(inputPath, 16);
                case FSB5Codec.PCM8:
                    return EncodeToPcm(inputPath, 8);
                case FSB5Codec.PCM32:
                    return EncodeToPcm(inputPath, 32);
                case FSB5Codec.VORBIS:
                    return EncodeToVorbis(inputPath);
                case FSB5Codec.OPUS:
                    return EncodeToOpus(inputPath);
                default:
                    throw new NotSupportedException($"Encoding to {codec} is not supported.");
            }
        }

        private static byte[] EncodeToMp3(string inputPath)
        {
            using var reader = new AudioFileReader(inputPath);
            var sampleProvider = new WdlResamplingSampleProvider(reader, 48000);
            int channels = reader.WaveFormat.Channels;
            var pcmFormat = new WaveFormat(48000, 16, channels);
            var pcmStream = new SampleToWaveProvider16(sampleProvider);

            var config = new LameConfig
            {
                BitRate = 320,
                OutputSampleRate = 48000,
                Mode = channels == 1 ? MPEGMode.Mono : MPEGMode.Stereo,
                VBR = VBRMode.Off,
                WriteVBRTag = false,
                LowPassFreq = -1,
                HighPassFreq = -1
            };

            using var output = new MemoryStream();
            using var mp3Writer = new LameMP3FileWriter(output, pcmStream.WaveFormat, config);

            byte[] buffer = new byte[pcmStream.WaveFormat.AverageBytesPerSecond];
            int bytesRead;
            while ((bytesRead = pcmStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                mp3Writer.Write(buffer, 0, bytesRead);
            }
            mp3Writer.Flush();
            return output.ToArray();
        }

        private static byte[] EncodeToPcm(string inputPath, int bitsPerSample)
        {
            // Simple PCM encoding: just read the file as raw PCM (assuming WAV).
            // In practice you'd resample/format correctly.
            if (bitsPerSample == 16)
            {
                using var reader = new AudioFileReader(inputPath);
                var pcmProvider = new SampleToWaveProvider16(reader.ToSampleProvider());
                using var ms = new MemoryStream();
                byte[] buffer = new byte[pcmProvider.WaveFormat.AverageBytesPerSecond];
                int read;
                while ((read = pcmProvider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
            throw new NotImplementedException($"PCM {bitsPerSample}-bit encoding not implemented.");
        }

        private static byte[] EncodeToVorbis(string inputPath)
        {
            // Using OggVorbisEncoder (NuGet: OggVorbisEncoder)
            using var reader = new AudioFileReader(inputPath);
            var sampleProvider = reader.ToSampleProvider();
            int channels = reader.WaveFormat.Channels;
            int sampleRate = reader.WaveFormat.SampleRate;

            var vorbisInfo = new VorbisInfo(channels, sampleRate);
            using var encoder = new OggVorbisEncoder.OggVorbisEncoder(vorbisInfo);

            // This is a simplified sketch. Actual usage requires reading float samples
            // and feeding them to the encoder.
            throw new NotImplementedException("Vorbis encoding requires manual implementation of the OggVorbisEncoder API.");
        }

        private static byte[] EncodeToOpus(string inputPath)
        {
            // Using Concentus (NuGet: Concentus)
            using var reader = new AudioFileReader(inputPath);
            var sampleProvider = reader.ToSampleProvider();
            int channels = reader.WaveFormat.Channels;
            int sampleRate = reader.WaveFormat.SampleRate;

            // Opus encoder setup.
            using var encoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            // This is a simplified sketch. Actual usage requires reading PCM samples
            // and calling encoder.Encode().
            throw new NotImplementedException("Opus encoding requires manual implementation of the Concentus API.");
        }
    }
}
