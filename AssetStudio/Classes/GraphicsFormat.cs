namespace AssetStudio
{
    // UnityEngine.Experimental.Rendering.GraphicsFormat, used by Texture2DArray, Texture3D and CubemapArray since 2019.1
    public enum GraphicsFormat
    {
        None = 0,
        R8_SRGB = 1,
        R8G8_SRGB = 2,
        R8G8B8_SRGB = 3,
        R8G8B8A8_SRGB = 4,
        R8_UNorm = 5,
        R8G8_UNorm = 6,
        R8G8B8_UNorm = 7,
        R8G8B8A8_UNorm = 8,
        R8_SNorm = 9,
        R8G8_SNorm = 10,
        R8G8B8_SNorm = 11,
        R8G8B8A8_SNorm = 12,
        R8_UInt = 13,
        R8G8_UInt = 14,
        R8G8B8_UInt = 15,
        R8G8B8A8_UInt = 16,
        R8_SInt = 17,
        R8G8_SInt = 18,
        R8G8B8_SInt = 19,
        R8G8B8A8_SInt = 20,
        R16_UNorm = 21,
        R16G16_UNorm = 22,
        R16G16B16_UNorm = 23,
        R16G16B16A16_UNorm = 24,
        R16_SNorm = 25,
        R16G16_SNorm = 26,
        R16G16B16_SNorm = 27,
        R16G16B16A16_SNorm = 28,
        R16_UInt = 29,
        R16G16_UInt = 30,
        R16G16B16_UInt = 31,
        R16G16B16A16_UInt = 32,
        R16_SInt = 33,
        R16G16_SInt = 34,
        R16G16B16_SInt = 35,
        R16G16B16A16_SInt = 36,
        R32_UInt = 37,
        R32G32_UInt = 38,
        R32G32B32_UInt = 39,
        R32G32B32A32_UInt = 40,
        R32_SInt = 41,
        R32G32_SInt = 42,
        R32G32B32_SInt = 43,
        R32G32B32A32_SInt = 44,
        R16_SFloat = 45,
        R16G16_SFloat = 46,
        R16G16B16_SFloat = 47,
        R16G16B16A16_SFloat = 48,
        R32_SFloat = 49,
        R32G32_SFloat = 50,
        R32G32B32_SFloat = 51,
        R32G32B32A32_SFloat = 52,
        B8G8R8_SRGB = 56,
        B8G8R8A8_SRGB = 57,
        B8G8R8_UNorm = 58,
        B8G8R8A8_UNorm = 59,
        B8G8R8_SNorm = 60,
        B8G8R8A8_SNorm = 61,
        B8G8R8_UInt = 62,
        B8G8R8A8_UInt = 63,
        B8G8R8_SInt = 64,
        B8G8R8A8_SInt = 65,
        R4G4B4A4_UNormPack16 = 66,
        B4G4R4A4_UNormPack16 = 67,
        R5G6B5_UNormPack16 = 68,
        B5G6R5_UNormPack16 = 69,
        R5G5B5A1_UNormPack16 = 70,
        B5G5R5A1_UNormPack16 = 71,
        A1R5G5B5_UNormPack16 = 72,
        E5B9G9R9_UFloatPack32 = 73,
        B10G11R11_UFloatPack32 = 74,
        A2B10G10R10_UNormPack32 = 75,
        A2B10G10R10_UIntPack32 = 76,
        A2B10G10R10_SIntPack32 = 77,
        A2R10G10B10_UNormPack32 = 78,
        A2R10G10B10_UIntPack32 = 79,
        A2R10G10B10_SIntPack32 = 80,
        A2R10G10B10_XRSRGBPack32 = 81,
        A2R10G10B10_XRUNormPack32 = 82,
        R10G10B10_XRSRGBPack32 = 83,
        R10G10B10_XRUNormPack32 = 84,
        A10R10G10B10_XRSRGBPack32 = 85,
        A10R10G10B10_XRUNormPack32 = 86,
        D16_UNorm = 90,
        D24_UNorm = 91,
        D24_UNorm_S8_UInt = 92,
        D32_SFloat = 93,
        D32_SFloat_S8_UInt = 94,
        S8_UInt = 95,
        RGBA_DXT1_SRGB = 96,
        RGBA_DXT1_UNorm = 97,
        RGBA_DXT3_SRGB = 98,
        RGBA_DXT3_UNorm = 99,
        RGBA_DXT5_SRGB = 100,
        RGBA_DXT5_UNorm = 101,
        R_BC4_UNorm = 102,
        R_BC4_SNorm = 103,
        RG_BC5_UNorm = 104,
        RG_BC5_SNorm = 105,
        RGB_BC6H_UFloat = 106,
        RGB_BC6H_SFloat = 107,
        RGBA_BC7_SRGB = 108,
        RGBA_BC7_UNorm = 109,
        RGB_PVRTC_2Bpp_SRGB = 110,
        RGB_PVRTC_2Bpp_UNorm = 111,
        RGB_PVRTC_4Bpp_SRGB = 112,
        RGB_PVRTC_4Bpp_UNorm = 113,
        RGBA_PVRTC_2Bpp_SRGB = 114,
        RGBA_PVRTC_2Bpp_UNorm = 115,
        RGBA_PVRTC_4Bpp_SRGB = 116,
        RGBA_PVRTC_4Bpp_UNorm = 117,
        RGB_ETC_UNorm = 118,
        RGB_ETC2_SRGB = 119,
        RGB_ETC2_UNorm = 120,
        RGB_A1_ETC2_SRGB = 121,
        RGB_A1_ETC2_UNorm = 122,
        RGBA_ETC2_SRGB = 123,
        RGBA_ETC2_UNorm = 124,
        R_EAC_UNorm = 125,
        R_EAC_SNorm = 126,
        RG_EAC_UNorm = 127,
        RG_EAC_SNorm = 128,
        RGBA_ASTC4X4_SRGB = 129,
        RGBA_ASTC4X4_UNorm = 130,
        RGBA_ASTC5X5_SRGB = 131,
        RGBA_ASTC5X5_UNorm = 132,
        RGBA_ASTC6X6_SRGB = 133,
        RGBA_ASTC6X6_UNorm = 134,
        RGBA_ASTC8X8_SRGB = 135,
        RGBA_ASTC8X8_UNorm = 136,
        RGBA_ASTC10X10_SRGB = 137,
        RGBA_ASTC10X10_UNorm = 138,
        RGBA_ASTC12X12_SRGB = 139,
        RGBA_ASTC12X12_UNorm = 140,
        YUV2 = 141,
        RGBA_ASTC4X4_UFloat = 145,
        RGBA_ASTC5X5_UFloat = 146,
        RGBA_ASTC6X6_UFloat = 147,
        RGBA_ASTC8X8_UFloat = 148,
        RGBA_ASTC10X10_UFloat = 149,
        RGBA_ASTC12X12_UFloat = 150,
        D16_UNorm_S8_UInt = 151,
    }

    public static class GraphicsFormatExtensions
    {
        /// <summary>
        /// The TextureFormat with the same memory layout, so the texture decoders can be used. 0 when there is none.
        /// </summary>
        public static TextureFormat ToTextureFormat(this GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.R8_SRGB:
                case GraphicsFormat.R8_UNorm:
                case GraphicsFormat.R8_UInt:
                    return TextureFormat.R8;
                case GraphicsFormat.R8G8_SRGB:
                case GraphicsFormat.R8G8_UNorm:
                case GraphicsFormat.R8G8_UInt:
                    return TextureFormat.RG16;
                case GraphicsFormat.R8G8B8_SRGB:
                case GraphicsFormat.R8G8B8_UNorm:
                case GraphicsFormat.R8G8B8_UInt:
                    return TextureFormat.RGB24;
                case GraphicsFormat.R8G8B8A8_SRGB:
                case GraphicsFormat.R8G8B8A8_UNorm:
                case GraphicsFormat.R8G8B8A8_UInt:
                    return TextureFormat.RGBA32;
                case GraphicsFormat.B8G8R8A8_SRGB:
                case GraphicsFormat.B8G8R8A8_UNorm:
                case GraphicsFormat.B8G8R8A8_UInt:
                    return TextureFormat.BGRA32;
                case GraphicsFormat.R16_UNorm:
                case GraphicsFormat.R16_UInt:
                    return TextureFormat.R16;
                case GraphicsFormat.R16G16_UNorm:
                case GraphicsFormat.R16G16_UInt:
                    return TextureFormat.RG32;
                case GraphicsFormat.R16G16B16_UNorm:
                case GraphicsFormat.R16G16B16_UInt:
                    return TextureFormat.RGB48;
                case GraphicsFormat.R16G16B16A16_UNorm:
                case GraphicsFormat.R16G16B16A16_UInt:
                    return TextureFormat.RGBA64;
                case GraphicsFormat.R16_SFloat:
                    return TextureFormat.RHalf;
                case GraphicsFormat.R16G16_SFloat:
                    return TextureFormat.RGHalf;
                case GraphicsFormat.R16G16B16A16_SFloat:
                    return TextureFormat.RGBAHalf;
                case GraphicsFormat.R32_SFloat:
                    return TextureFormat.RFloat;
                case GraphicsFormat.R32G32_SFloat:
                    return TextureFormat.RGFloat;
                case GraphicsFormat.R32G32B32A32_SFloat:
                    return TextureFormat.RGBAFloat;
                case GraphicsFormat.R4G4B4A4_UNormPack16:
                    return TextureFormat.RGBA4444;
                case GraphicsFormat.R5G6B5_UNormPack16:
                    return TextureFormat.RGB565;
                case GraphicsFormat.E5B9G9R9_UFloatPack32:
                    return TextureFormat.RGB9e5Float;
                case GraphicsFormat.RGBA_DXT1_SRGB:
                case GraphicsFormat.RGBA_DXT1_UNorm:
                    return TextureFormat.DXT1;
                case GraphicsFormat.RGBA_DXT3_SRGB:
                case GraphicsFormat.RGBA_DXT3_UNorm:
                    return TextureFormat.DXT3;
                case GraphicsFormat.RGBA_DXT5_SRGB:
                case GraphicsFormat.RGBA_DXT5_UNorm:
                    return TextureFormat.DXT5;
                case GraphicsFormat.R_BC4_UNorm:
                    return TextureFormat.BC4;
                case GraphicsFormat.RG_BC5_UNorm:
                    return TextureFormat.BC5;
                case GraphicsFormat.RGB_BC6H_UFloat:
                    return TextureFormat.BC6H;
                case GraphicsFormat.RGBA_BC7_SRGB:
                case GraphicsFormat.RGBA_BC7_UNorm:
                    return TextureFormat.BC7;
                case GraphicsFormat.RGB_PVRTC_2Bpp_SRGB:
                case GraphicsFormat.RGB_PVRTC_2Bpp_UNorm:
                    return TextureFormat.PVRTC_RGB2;
                case GraphicsFormat.RGBA_PVRTC_2Bpp_SRGB:
                case GraphicsFormat.RGBA_PVRTC_2Bpp_UNorm:
                    return TextureFormat.PVRTC_RGBA2;
                case GraphicsFormat.RGB_PVRTC_4Bpp_SRGB:
                case GraphicsFormat.RGB_PVRTC_4Bpp_UNorm:
                    return TextureFormat.PVRTC_RGB4;
                case GraphicsFormat.RGBA_PVRTC_4Bpp_SRGB:
                case GraphicsFormat.RGBA_PVRTC_4Bpp_UNorm:
                    return TextureFormat.PVRTC_RGBA4;
                case GraphicsFormat.RGB_ETC_UNorm:
                    return TextureFormat.ETC_RGB4;
                case GraphicsFormat.RGB_ETC2_SRGB:
                case GraphicsFormat.RGB_ETC2_UNorm:
                    return TextureFormat.ETC2_RGB;
                case GraphicsFormat.RGB_A1_ETC2_SRGB:
                case GraphicsFormat.RGB_A1_ETC2_UNorm:
                    return TextureFormat.ETC2_RGBA1;
                case GraphicsFormat.RGBA_ETC2_SRGB:
                case GraphicsFormat.RGBA_ETC2_UNorm:
                    return TextureFormat.ETC2_RGBA8;
                case GraphicsFormat.R_EAC_UNorm:
                    return TextureFormat.EAC_R;
                case GraphicsFormat.R_EAC_SNorm:
                    return TextureFormat.EAC_R_SIGNED;
                case GraphicsFormat.RG_EAC_UNorm:
                    return TextureFormat.EAC_RG;
                case GraphicsFormat.RG_EAC_SNorm:
                    return TextureFormat.EAC_RG_SIGNED;
                case GraphicsFormat.RGBA_ASTC4X4_SRGB:
                case GraphicsFormat.RGBA_ASTC4X4_UNorm:
                    return TextureFormat.ASTC_RGBA_4x4;
                case GraphicsFormat.RGBA_ASTC5X5_SRGB:
                case GraphicsFormat.RGBA_ASTC5X5_UNorm:
                    return TextureFormat.ASTC_RGBA_5x5;
                case GraphicsFormat.RGBA_ASTC6X6_SRGB:
                case GraphicsFormat.RGBA_ASTC6X6_UNorm:
                    return TextureFormat.ASTC_RGBA_6x6;
                case GraphicsFormat.RGBA_ASTC8X8_SRGB:
                case GraphicsFormat.RGBA_ASTC8X8_UNorm:
                    return TextureFormat.ASTC_RGBA_8x8;
                case GraphicsFormat.RGBA_ASTC10X10_SRGB:
                case GraphicsFormat.RGBA_ASTC10X10_UNorm:
                    return TextureFormat.ASTC_RGBA_10x10;
                case GraphicsFormat.RGBA_ASTC12X12_SRGB:
                case GraphicsFormat.RGBA_ASTC12X12_UNorm:
                    return TextureFormat.ASTC_RGBA_12x12;
                case GraphicsFormat.RGBA_ASTC4X4_UFloat:
                    return TextureFormat.ASTC_HDR_4x4;
                case GraphicsFormat.RGBA_ASTC5X5_UFloat:
                    return TextureFormat.ASTC_HDR_5x5;
                case GraphicsFormat.RGBA_ASTC6X6_UFloat:
                    return TextureFormat.ASTC_HDR_6x6;
                case GraphicsFormat.RGBA_ASTC8X8_UFloat:
                    return TextureFormat.ASTC_HDR_8x8;
                case GraphicsFormat.RGBA_ASTC10X10_UFloat:
                    return TextureFormat.ASTC_HDR_10x10;
                case GraphicsFormat.RGBA_ASTC12X12_UFloat:
                    return TextureFormat.ASTC_HDR_12x12;
                case GraphicsFormat.YUV2:
                    return TextureFormat.YUY2;
                default:
                    return 0;
            }
        }
    }
}
