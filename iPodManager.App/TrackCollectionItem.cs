using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace iPodManager
{
    public class TrackCollectionItem : INotifyPropertyChanged
    {
        private string _name;

        public TrackCollectionItem(string name)
        {
            _name = name;
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

        public ObservableCollection<TrackItem> Tracks { get; } =
            new ObservableCollection<TrackItem>();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
