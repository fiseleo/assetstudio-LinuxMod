using System.ComponentModel;

namespace AssetStudio.Avalonia
{
    public class AssetItem : INotifyPropertyChanged
    {
        public Object Asset;
        public SerializedFile SourceFile;
        public string TypeString { get; }
        public long m_PathID;
        public long FullSize { get; set; }
        public ClassIDType Type;
        public string InfoText;
        public string UniqueID;
        public SceneNode TreeNode;

        private string text;
        private string container = string.Empty;

        public event PropertyChangedEventHandler PropertyChanged;

        public AssetItem(Object asset)
        {
            Asset = asset;
            text = asset.Name;
            SourceFile = asset.assetsFile;
            Type = asset.type;
            TypeString = Type.ToString();
            m_PathID = asset.m_PathID;
            FullSize = asset.byteSize;
        }

        public string Text
        {
            get => text;
            set
            {
                if (text != value)
                {
                    text = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
                }
            }
        }

        public string Name => Text;

        public string Container
        {
            get => container;
            set
            {
                if (container != value)
                {
                    container = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Container)));
                }
            }
        }

        public long PathID => m_PathID;
    }
}
