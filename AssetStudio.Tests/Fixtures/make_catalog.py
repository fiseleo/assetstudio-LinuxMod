"""Writes synthetic_catalog.json: a small Addressables JSON catalog (the format of Addressables 1.19+, internal id
prefixes, keys as objects, dependency keys as int hashes). The binary fixtures catalog_v1/v2/v3.bin were written from it
by the serializers of Addressables 1.21.21, 2.11.2 and 4.0.1."""
import base64, json, struct

prefix = '{UnityEngine.AddressableAssets.Addressables.RuntimePath}/StandaloneWindows64'
providers = ['UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider',
             'UnityEngine.ResourceManagement.ResourceProviders.BundledAssetProvider',
             'UnityEngine.ResourceManagement.ResourceProviders.SceneProvider']
types = [('Unity.ResourceManager, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null', 'UnityEngine.ResourceManagement.ResourceProviders.IAssetBundleResource'),
         ('UnityEngine.CoreModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null', 'UnityEngine.GameObject'),
         ('UnityEngine.CoreModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null', 'UnityEngine.Texture2D'),
         ('Unity.ResourceManager, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null', 'UnityEngine.ResourceManagement.ResourceProviders.SceneInstance')]
# (internal id, provider, type, keys (first = primary), dependency key, bundle options)
locations = [
    (prefix + '/characters_assets_all_0123456789abcdef0123456789abcdef.bundle', 0, 0, ['characters_assets_all.bundle'], None,
     dict(m_Hash='0123456789abcdef0123456789abcdef', m_Crc=1234567, m_BundleName='characters_assets_all', m_BundleSize=56789)),
    (prefix + '/shared_assets_all_fedcba9876543210fedcba9876543210.bundle', 0, 0, ['shared_assets_all.bundle'], None,
     dict(m_Hash='fedcba9876543210fedcba9876543210', m_Crc=7654321, m_BundleName='shared_assets_all', m_BundleSize=98765)),
    ('Assets/Characters/Hero.prefab', 1, 1, ['Hero', '11112222333344445555666677778888', 'Characters', 'Playable'], 1001, None),
    ('Assets/Textures/Hero_Albedo.png', 1, 2, ['HeroAlbedo', '9999aaaabbbbccccddddeeeeffff0000', 'Textures'], 1002, None),
    ('Assets/Scenes/Level1.unity', 2, 3, ['Level1', 'aaaa1111bbbb2222cccc3333dddd4444'], 1003, None),
]
dependencies = {1001: [0, 1], 1002: [0], 1003: [1]}

def obj(value):
    if isinstance(value, int):
        return bytes([4]) + struct.pack('<i', value)
    data = value.encode('ascii')
    return bytes([0]) + struct.pack('<i', len(data)) + data

keys = []  # unique keys, in order
for loc in locations:
    for k in loc[3]:
        if k not in keys: keys.append(k)
for k in dependencies:
    keys.append(k)
buckets = {k: [] for k in keys}
for i, loc in enumerate(locations):
    for k in loc[3]:
        buckets[k].append(i)
for k, locs in dependencies.items():
    buckets[k] = locs

key_data = struct.pack('<i', len(keys)); bucket_data = struct.pack('<i', len(keys))
for k in keys:
    bucket_data += struct.pack('<ii', len(key_data), len(buckets[k])) + b''.join(struct.pack('<i', e) for e in buckets[k])
    key_data += obj(k)
extra = b''; entries = struct.pack('<i', len(locations)); internal_ids = []
for i, (iid, provider, type_index, loc_keys, dep, options) in enumerate(locations):
    if iid.startswith(prefix):
        stored = '0#' + iid[len(prefix):]
    else:
        stored = iid
    internal_ids.append(stored)
    data_index = -1
    if options:
        data_index = len(extra)
        asm = b'Unity.ResourceManager, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'
        cls = b'UnityEngine.ResourceManagement.ResourceProviders.AssetBundleRequestOptions'
        text = json.dumps(dict(options, m_Timeout=0, m_ChunkedTransfer=False, m_RedirectLimit=-1, m_RetryCount=0)).encode('utf-16-le')
        extra += bytes([7, len(asm)]) + asm + bytes([len(cls)]) + cls + struct.pack('<i', len(text)) + text
    dep_index = keys.index(dep) if dep is not None else -1
    entries += struct.pack('<7i', i, provider, dep_index, 0, data_index, keys.index(loc_keys[0]), type_index)

catalog = {
    'm_LocatorId': 'AddressablesMainContentCatalog',
    'm_BuildResultHash': '5d1b0d6fd0bbd6c9a8e8d3b3b8d8c0aa',
    'm_InstanceProviderData': {'m_Id': 'UnityEngine.ResourceManagement.ResourceProviders.InstanceProvider', 'm_ObjectType': {'m_AssemblyName': types[0][0], 'm_ClassName': 'UnityEngine.ResourceManagement.ResourceProviders.InstanceProvider'}, 'm_Data': ''},
    'm_SceneProviderData': {'m_Id': 'UnityEngine.ResourceManagement.ResourceProviders.SceneProvider', 'm_ObjectType': {'m_AssemblyName': types[0][0], 'm_ClassName': 'UnityEngine.ResourceManagement.ResourceProviders.SceneProvider'}, 'm_Data': ''},
    'm_ResourceProviderData': [],
    'm_ProviderIds': providers,
    'm_InternalIds': internal_ids,
    'm_KeyDataString': base64.b64encode(key_data).decode(),
    'm_BucketDataString': base64.b64encode(bucket_data).decode(),
    'm_EntryDataString': base64.b64encode(entries).decode(),
    'm_ExtraDataString': base64.b64encode(extra).decode(),
    'm_resourceTypes': [{'m_AssemblyName': a, 'm_ClassName': c} for a, c in types],
    'm_InternalIdPrefixes': [prefix],
}
json.dump(catalog, open('synthetic_catalog.json', 'w'), indent=1)
