namespace FreeFlume.ViewModels
{
    public partial class MainViewModel() : BaseViewModel("Home")
    {
        private int _count;

        [ObservableProperty]
        public partial string CountText { get; set; } = "Current count: 0";

        [RelayCommand]
        private void Increment()
        {
            _count++;
            CountText = $"Current count: {_count}";
        }
    }
}
