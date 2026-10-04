using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace iPodManager
{
    public class CategoryItem : INotifyPropertyChanged
    {
        private string _name;
        private string _sizeText;
        private long _sizeBytes;
        private string _iconSource;

        public CategoryItem(string name, string sizeText, string iconSource)
        {
            _name = name;
            _sizeText = sizeText;
            _iconSource = iconSource;
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

        public long SizeBytes
        {
            get => _sizeBytes;
            set
            {
                if (_sizeBytes == value)
                    return;

                _sizeBytes = value;
                OnPropertyChanged();
            }
        }

        public string IconSource
        {
            get => _iconSource;
            set
            {
                if (_iconSource == value)
                    return;

                _iconSource = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<TrackCollectionItem> TrackCollections { get; } =
            new ObservableCollection<TrackCollectionItem>();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
