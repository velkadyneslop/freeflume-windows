namespace FreeFlume.ViewModels
{
    public partial class BaseViewModel(string title = "") : ObservableObject
    {
        [ObservableProperty]
        public partial string Title { get; set; } = title;
    }
}
