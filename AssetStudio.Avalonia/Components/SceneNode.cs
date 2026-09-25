using System.Collections.Generic;
using System.ComponentModel;

namespace AssetStudio.Avalonia
{
    /// <summary>
    /// Scene hierarchy node. Replaces WinForms TreeNode / GameObjectTreeNode.
    /// </summary>
    public class SceneNode : INotifyPropertyChanged
    {
        private bool isChecked;
        private bool isExpanded;

        public string Text { get; }
        public GameObject gameObject { get; }
        public SceneNode Parent { get; private set; }
        public List<SceneNode> Nodes { get; } = new List<SceneNode>();
        public bool HasModel { get; }

        public event PropertyChangedEventHandler PropertyChanged;

        public SceneNode(string text)
        {
            Text = text;
        }

        public SceneNode(GameObject gameObject)
        {
            this.gameObject = gameObject;
            Text = gameObject.m_Name;
            HasModel = gameObject.HasModel();
        }

        public int Level
        {
            get
            {
                var level = 0;
                for (var node = Parent; node != null; node = node.Parent)
                {
                    level++;
                }
                return level;
            }
        }

        public void Add(SceneNode node)
        {
            node.Parent = this;
            Nodes.Add(node);
        }

        /// <summary>
        /// Checking a node checks all of its children, like the original TreeView AfterCheck handler.
        /// </summary>
        public bool IsChecked
        {
            get => isChecked;
            set
            {
                if (isChecked != value)
                {
                    isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
                foreach (var node in Nodes)
                {
                    node.IsChecked = value;
                }
            }
        }

        public bool IsExpanded
        {
            get => isExpanded;
            set
            {
                if (isExpanded != value)
                {
                    isExpanded = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                }
            }
        }

        public void EnsureVisible()
        {
            for (var node = Parent; node != null; node = node.Parent)
            {
                node.IsExpanded = true;
            }
        }

        public override string ToString() => Text;
    }
}
