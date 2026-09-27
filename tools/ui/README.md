# UI asset tools

`lucide2xaml.py` turns Lucide SVGs into `src/Jevdexer.App/Themes/Icons.xaml`. To add an icon:

```
npm pack lucide-static && tar -xzf lucide-static-*.tgz
python tools/ui/lucide2xaml.py package/icons src/Jevdexer.App/Themes/Icons.xaml <every icon name, old and new>
```

Names are Lucide's file names (`refresh-cw`); resource keys are `Icon.RefreshCw`. Pass the full list: the file is rewritten.
Current list: search settings refresh-cw power sun moon monitor app-window folder file layout-grid corner-down-left arrow-up arrow-down arrow-left sparkles wifi-off loader-circle check keyboard x history trash-2 shield-check scan-search folder-plus pause play lock cpu download compass arrow-right mouse-pointer-click
