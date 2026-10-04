using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace iPodManager
{
    public class TrackItem : INotifyPropertyChanged
    {
        private string _name;
        private string _artistText;
        private string _sizeText;
        private string _filePath;
        private string _annotation;
        private bool _isChecked;

        public TrackItem(
            string name,
            string sizeText = "0.00 MB",
            string filePath = "",
            string artistText = "",
            string annotation = "")
        {
            _name = name;
            _artistText = artistText;
            _sizeText = sizeText;
            _filePath = filePath;
            _annotation = annotation;
        }

        public string StableKey { get; init; } = string.Empty;
        public string Album { get; init; } = string.Empty;
        public uint TrackNumber { get; init; }
        public uint DiscNumber { get; init; }
        public LibraryCategory Category { get; init; }
        public int? StockIndex { get; init; }
        public Guid? SourceId { get; init; }
        public string RelativeSourcePath { get; init; } = string.Empty;
        public byte[]? SourceSha256 { get; init; }
        public DeployedTrack? DeployedState { get; init; }
        public SourceState SourceState { get; init; } = SourceState.Available;
        public DeploymentHealth DeploymentHealth { get; init; } = DeploymentHealth.NotDeployed;
        public bool IsDeployed => DeployedState != null || StockIndex != null;

        public string Annotation
        {
            get => _annotation;
            set
            {
                if (_annotation == value)
                    return;

                _annotation = value;
                OnPropertyChanged();
            }
        }

        public string ArtistText
        {
            get => _artistText;
            set
            {
                if (_artistText == value)
                    return;

                _artistText = value;
                OnPropertyChanged();
            }
        }

        public string SizeText
        {
            get => _sizeText;
            set
            {
                if (_sizeText == value)
                    return;

                _sizeText = value;
                OnPropertyChanged();
            }
        }

        public string FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath == value)
                    return;

                _filePath = value;
                OnPropertyChanged();
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value)
                    return;

                _name = value;
                OnPropertyChanged();
            }
        }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                    return;

                _isChecked = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
