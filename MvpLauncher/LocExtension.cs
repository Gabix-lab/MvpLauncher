using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace MvpLauncher
{
    public class LocExtension : MarkupExtension
    {
        public string Key { get; set; } = "";

        public LocExtension() { }
        public LocExtension(string key) { Key = key; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new Binding($"[{Key}]")
            {
                Source = TranslationSource.Instance,
                Mode = BindingMode.OneWay
            };

            return binding.ProvideValue(serviceProvider);
        }
    }
}