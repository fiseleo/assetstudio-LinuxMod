using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio
{
    public enum SerializedFileFormatVersion
    {
        Unsupported = 1,
        Unknown_2 = 2,
        Unknown_3 = 3,
        /// <summary>
        /// 1.2.0 to 2.0.0
        /// </summary>
        Unknown_5 = 5,
        /// <summary>
        /// 2.1.0 to 2.6.1
        /// </summary>
        Unknown_6 = 6,
        /// <summary>
        /// 3.0.0b
        /// </summary>
        Unknown_7 = 7,
        /// <summary>
        /// 3.0.0 to 3.4.2
        /// </summary>
        Unknown_8 = 8,
        /// <summary>
        /// 3.5.0 to 4.7.2
        /// </summary>
        Unknown_9 = 9,
        /// <summary>
        /// 5.0.0aunk1
        /// </summary>
        Unknown_10 = 10,
        /// <summary>
        /// 5.0.0aunk2
        /// </summary>
        HasScriptTypeIndex = 11,
        /// <summary>
        /// 5.0.0aunk3
        /// </summary>
        Unknown_12 = 12,
        /// <summary>
        /// 5.0.0aunk4
        /// </summary>
        HasTypeTreeHashes = 13,
        /// <summary>
        /// 5.0.0unk
        /// </summary>
        Unknown_14 = 14,
        /// <summary>
        /// 5.0.1 to 5.4.0
        /// </summary>
        SupportsStrippedObject = 15,
        /// <summary>
        /// 5.5.0a
        /// </summary>
        RefactoredClassId = 16,
        /// <summary>
        /// 5.5.0unk to 2018.4
        /// </summary>
        RefactorTypeData = 17,
        /// <summary>
        /// 2019.1a
        /// </summary>
        RefactorShareableTypeTreeData = 18,
        /// <summary>
        /// 2019.1unk
        /// </summary>
        TypeTreeNodeWithTypeFlags = 19,
        /// <summary>
        /// 2019.2
        /// </summary>
        SupportsRefObject = 20,
        /// <summary>
        /// 2019.3 to 2019.4
        /// </summary>
        StoresTypeDependencies = 21,
        /// <summary>
        /// 2020.1 to 2023.x / 6000.x (Unity 6)
        /// </summary>
        LargeFilesSupport = 22,

        /// <summary>
        /// 6000.5: each type stores an extra 16-byte type tree hash and its type tree as a size-prefixed "mhtt" blob.
        /// A size of 0 means the type trees were extracted to a separate file (Addressables "Extract Typetrees").
        /// </summary>
        TypeTreeBlobs = 23,

        Unknown_24 = 24,

        Unknown_25 = 25,

        /// <summary>
        /// 6000.7: type trees are split into shareable sub trees. The "mhtt" blob of a type ends with the hashes of
        /// the sub trees it uses; nodes with type flag 0x20 stand for one of them. The sub trees follow the
        /// reference types in one table.
        /// </summary>
        SharedTypeTrees = 26
    }
}
