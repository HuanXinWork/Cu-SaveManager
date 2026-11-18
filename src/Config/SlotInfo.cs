using System.IO;

namespace CasualtiesUnknown.SaveManager
{
    /// <summary>UI 槽位视图模型，封装主存档文件、伴生元数据及懒加载的 MD5 哈希。</summary>
    internal sealed class SlotInfo
    {
        internal FileInfo File { get; }
        internal SlotSidecar Sidecar { get; }
        internal string DateFolder { get; }
        internal string RunIdFolder { get; }

        private string _hash;
        private bool _hashComputed;

        internal SlotInfo(FileInfo file, SlotSidecar sidecar, string dateFolder, string runIdFolder)
        {
            File = file;
            Sidecar = sidecar;
            DateFolder = dateFolder;
            RunIdFolder = runIdFolder;
        }

        internal string DisplayName
            => string.IsNullOrEmpty(Sidecar.Nickname) ? Path.GetFileNameWithoutExtension(File.Name) : Sidecar.Nickname;

        internal string FullSlotPath => File.FullName;

        internal string Hash
        {
            get
            {
                if (!_hashComputed)
                {
                    _hash = SaveStore.ComputeFileHash(FullSlotPath);
                    _hashComputed = true;
                }
                return _hash;
            }
        }
    }
}
