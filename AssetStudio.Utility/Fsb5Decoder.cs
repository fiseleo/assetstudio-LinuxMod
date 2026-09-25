using Fmod5Sharp;
using System;

namespace AssetStudio
{
    /// <summary>
    /// Pure managed FSB5 decoder (Fmod5Sharp) used when the native FMOD library is not available.
    /// Produces a standard file: .ogg for Vorbis, .wav for PCM / ADPCM.
    /// </summary>
    public static class Fsb5Decoder
    {
        public static bool TryConvert(AudioClip m_AudioClip, out byte[] data, out string extension)
        {
            data = null;
            extension = null;
            try
            {
                var audioData = m_AudioClip.m_AudioData.GetData();
                if (audioData == null || audioData.Length < 4)
                    return false;
                // Unity 5+ stores FSB5 banks ("FSB5" magic)
                if (audioData[0] != 'F' || audioData[1] != 'S' || audioData[2] != 'B' || audioData[3] != '5')
                    return false;
                if (!FsbLoader.TryLoadFsbFromByteArray(audioData, out var bank) || bank == null || bank.Samples.Count == 0)
                    return false;
                if (!bank.Samples[0].RebuildAsStandardFileFormat(out data, out extension) || data == null)
                    return false;
                extension = "." + extension.TrimStart('.');
                return true;
            }
            catch (Exception e)
            {
                Logger.Warning($"Fmod5Sharp could not decode {m_AudioClip.m_Name}: {e.Message}");
                data = null;
                return false;
            }
        }
    }
}
