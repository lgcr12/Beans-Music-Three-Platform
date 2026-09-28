param([string]$Action='inspect',[string]$Name='', [int]$Width=0,[int]$Height=0,[string]$Output='current',[string]$Value='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @"
using System;using System.Runtime.InteropServices;public class AnimeNative {
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int height,bool repaint);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
}
"@
$dir='D:\Code\Beans-Music\windows\Beans.Windows.Rebuild\artifacts\anime-validation'
$taskProcess=Get-Process -Id ([int](Get-Content "$dir\process-id.txt"))
$root=[System.Windows.Automation.AutomationElement]::FromHandle($taskProcess.MainWindowHandle)
if($Action -eq 'click'){
 $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$Name)
 $targets=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
 $target=@($targets | Where-Object {-not $_.Current.IsOffscreen -and $_.Current.IsEnabled}) | Select-Object -First 1
 if(!$target){throw "Not found: $Name"}
  $pattern=$null
 for($i=0;$i -lt 5;$i++){
  if($target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pattern)){$pattern.Invoke();break}
  if($target.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)){$pattern.Toggle();break}
  if($target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)){$pattern.Select();break}
  $target=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($target)
 }
 'invoked '+$Name
}elseif($Action -eq 'text'){
 $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$Name)
 $target=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
 $target.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}elseif($Action -eq 'scrolltop' -or $Action -eq 'scrollbottom'){
 $all=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 foreach($element in $all){$pattern=$null;if(-not $element.Current.IsOffscreen -and $element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern,[ref]$pattern)){if($pattern.Current.VerticallyScrollable){$position=0;if($Action -eq 'scrollbottom'){$position=100};$pattern.SetScrollPercent(-1,$position);break}}}
}elseif($Action -eq 'bounds'){
 $r=$root.Current.BoundingRectangle
 $all=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 $overflow=@($all | Where-Object {-not $_.Current.IsOffscreen -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and ($_.Current.BoundingRectangle.Right -gt $r.Right+1 -or $_.Current.BoundingRectangle.Left -lt $r.Left-1)})
 "Visible buttons outside horizontal window bounds: $($overflow.Count)"
 $overflow | ForEach-Object {$_.Current.Name}
}elseif($Action -eq 'resize'){
 [AnimeNative]::MoveWindow($taskProcess.MainWindowHandle,20,20,$Width,$Height,$true)|Out-Null
}elseif($Action -eq 'capture'){
 [AnimeNative]::SetProcessDPIAware()|Out-Null
 [AnimeNative]::SetForegroundWindow($taskProcess.MainWindowHandle)|Out-Null
 $r=$root.Current.BoundingRectangle
 $bitmap=New-Object System.Drawing.Bitmap([int]$r.Width,[int]$r.Height)
 $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
 $dc=$graphics.GetHdc()
 try { if(-not [AnimeNative]::PrintWindow($taskProcess.MainWindowHandle,$dc,2)){throw 'Window capture failed'} } finally {$graphics.ReleaseHdc($dc)}
 $bitmap.Save("$dir\$Output.png");$graphics.Dispose();$bitmap.Dispose()
 "captured $Output | $($r.Width)x$($r.Height) | DPI $([AnimeNative]::GetDpiForWindow($taskProcess.MainWindowHandle))"
}else{
 $all=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 $all | Where-Object {$_.Current.Name -and -not $_.Current.IsOffscreen} | ForEach-Object {"$($_.Current.ControlType.ProgrammaticName) | $($_.Current.Name) | $($_.Current.BoundingRectangle)"} | Select-Object -First 90
}
