"""Record current WPF controls/input evidence and their proposed UI families."""
from pathlib import Path
import csv, hashlib, json, re, xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[3]
OUT=ROOT/'docs/ui-design-system'
MAP={
 'AssemblyInfo':('Application','Theme registration metadata'), 'App':('Application','Theme / app resources'), 'MainWindow':('Tape / QuickDisk','DocumentWorkspace'), 'MainWindowQuickDiskVisual':('QuickDisk','QuickDiskWorkspace'),
 'DskEditorWindow':('Disk / HFE','DiskWorkspace'), 'DskEditorSectorSelection':('Disk / HFE','SelectionCoordinator'),'DskEditorVisualAnalysis':('Disk / HFE','InspectorCoordinator'),
 'AudioSignalAnalyzerWindow':('Audio','AudioAnalysisView'),'AudioSignalAnalyzerExport':('Audio','ExportPreview'),'AudioSignalAnalyzerSelection':('Audio','SelectionCoordinator'),'AudioSignalCharts':('Audio','SignalPlot / HistogramPlot / DriftPlot'),
 'AudioRegionRecoveryWindow':('Audio recovery','RecoveryView'),'AudioRegionRecoveryComparison':('Audio recovery','RecoveryComparison'),'AudioRegionRecoveryExport':('Audio recovery','RecoveredArtifactExport'),'AudioRegionRecoveryHeaderless':('Audio recovery','HeaderlessRecovery'),'AudioRegionRecoveryPreview':('Audio recovery','CandidatePreview'),
 'AudioPulseReferenceControl':('Audio / Recorder','PulseReferencePicker'),'AudioRecorderWindow':('Recorder','RecorderView'),'AudioRecorderNavigation':('Recorder','RecordingNavigation'),
 'LiveSignalCanvas':('Recorder','SignalPlot / SpectrumPlot'),'LivePulsePanel':('Recorder','LivePulseView'),'HeadAlignmentPanel':('Recorder','HeadAlignmentView'),'HeadEdgeAdjustmentControl':('Recorder','RelativeEdgeDial'),
 'BatchProcessDialog':('Batch','BatchWorkspace'),'BatchFileDetailsDialog':('Batch','ReportDrawer'),'BootstrapDialog':('Disk boot','BootstrapEditor'),
 'CompressionOptionsControl':('Shared conversion','CompressionOptions'),'CompressionSettingsDialog':('Shared conversion','CompressionDialog'),
 'CpmLayoutDialog':('Disk filesystem','CpmLayoutEditor'),'CpmSystemBuilderDialog':('Disk boot','BootInstallPreview'),'CrossDiskTransferDialog':('Disk transfer','TransferPreview'),
 'DiskFormatting':('Disk formatting','ChangePreview'),'DiskMapVisuals':('Shared','SelectionStyles'),'MapInteraction':('Shared','SelectionPolicy'),
 'DskCompareWindow':('Disk compare','CompareView / HexDiff'),'DskConversionDialog':('Disk conversion','ConversionExportPreview'),'DskDiskMapControl':('Disk / HFE','DiskMapRenderer'),
 'DskFilePropertiesControl':('Disk properties','PropertyEditor'),'DskFilePropertiesDialog':('Disk properties','PropertiesDialog'),
 'DskHexEditorWindow':('Disk hex','HexEditor'),'DskMfmDetailControl':('Disk physical','TrackCellRenderer'),'DskNewDialog':('Disk creation','NewDiskDialog'),
 'DskPatchPreviewWindow':('Disk changes','ChangePreview'),'DskPhysicalPropertiesDialog':('Disk properties','PhysicalPropertyEditor'),'DskRawRangeDialog':('Disk transfer','RawRangeTransfer'),
 'DskRepairDialog':('Disk repair','ContainerRepairPreview'),'DskStructureWindow':('Disk filesystem','StructureView'),'DskVisualCanvas':('Disk physical','DiskSurfaceRenderer'),
 'DskVisualInspectorWindow':('Disk inspection','InspectorHost'),'DskVisualInspectorDetails':('Disk inspection','InspectorDetails'),
 'HexBrowser':('Shared hex','ReadOnlyHexView'),'IplDskOptionsDialog':('Single IPL','SingleIplOptions'),'MultiGameIplDskOptionsDialog':('Multi IPL','MultiIplEditor'),
 'NativeComDialog':('Native COM','NativeComDialog'),'PhysicalTrackViewerWindow':('Physical media','PhysicalTrackView'),
 'QuickDiskInspectorControl':('QuickDisk hosts','HostInspector'),'QuickDiskMapControl':('QuickDisk logical','QuickDiskMapRenderer'),'QuickDiskSurfaceCanvas':('QuickDisk surface','QuickDiskSurfaceRenderer'),
 'QuickDiskVisualCanvas':('QuickDisk physical','QuickDiskPhysicalRenderer'),'QuickDiskVisualInspectorWindow':('QuickDisk inspection','InspectorHost'),
 'ReportView':('Shared reports','ReportViewer'),'SaveOptionsDialog':('Tape / QuickDisk','SaveExportOptions'),'SeparateExportDialog':('Shared export','MultiExportPreview'),
 'UserProfilesDialog':('Profiles','ProfilesView'),'WavAnalysisProgressWindow':('Audio progress','BackgroundTaskProgress'),'WavAnalysisStatisticsWindow':('Audio statistics','AudioStatistics'),
 'WavImportOptionsWindow':('Audio import','AudioImportOptions'),
}
rows=[];inputs=[];sources=[];unmapped=[]
for p in sorted(ROOT.glob('*.cs'))+sorted(ROOT.glob('*.xaml')):
    if p.name in {'MZTFileReader.cs','MZQFileReader.cs','QDFFileReader.cs'}:continue
    s=p.read_text(encoding='utf-8-sig');key=p.name.replace('.xaml.cs','').replace('.xaml','').replace('.cs','')
    if key not in MAP:
        if 'System.Windows' in s or p.suffix=='.xaml':unmapped.append(p.name)
        continue
    module,component=MAP[key];sources.append({'source':p.name,'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'module':module,'target':component})
    if p.suffix=='.xaml':
        tree=ET.fromstring(s)
        for n in tree.iter():
            kind=n.tag.split('}')[-1]
            if kind not in {'Button','MenuItem','TabItem','CheckBox','ComboBox','TextBox','DataGrid','Expander','Window','UserControl'}:continue
            a={k.split('}')[-1]:v for k,v in n.attrib.items()};name=a.get('Name',a.get('Class',''))
            caption=a.get('Content',a.get('Header',a.get('Title','')));line=next((i for i,t in enumerate(s.splitlines(),1) if name and name in t),1)
            rows.append([module,p.name,line,kind,name,caption,a.get('Click',a.get('SelectionChanged','')),a.get('InputGestureText',''),a.get('ToolTip',''),component,'Mapped family; detailed workflow in PARITY.md'])
    else:
        for i,line in enumerate(s.splitlines(),1):
            for m in re.finditer(r'\b(Content|Header|ToolTip)\s*=\s*"((?:[^"\\]|\\.)*)"|\b(Button|Check|Choice|AddButton|AddTab)\(\s*"((?:[^"\\]|\\.)*)"',line):
                kind=m.group(1) or m.group(3);label=m.group(2) if m.group(1) else m.group(4)
                rows.append([module,p.name,i,kind,'',label,'','',label if kind=='ToolTip' else '',component,'Literal evidence; dynamic expressions require runtime parity checks'])
            if re.search(r'\bKey\.|\bModifierKeys\.|OnMouseWheel|MouseWheel\s*\+=|OnMouseLeftButtonDown|MouseLeftButtonDown\s*\+=',line):inputs.append([module,p.name,i,line.strip(),component,'See INTERACTIONS.md for proposed shared policy'])
with (OUT/'wpf-controls.csv').open('w',encoding='utf-8-sig',newline='') as f:
    w=csv.writer(f);w.writerow(['Module','Source','Line','ControlOrProperty','Name','CurrentCaption','CurrentHandler','CurrentShortcutHint','CurrentTooltip','ProposedComponent','MappingNote']);w.writerows(rows)
with (OUT/'wpf-inputs.csv').open('w',encoding='utf-8-sig',newline='') as f:
    w=csv.writer(f);w.writerow(['Module','Source','Line','CurrentInputEvidence','ProposedComponent','ProposedPolicy']);w.writerows(inputs)
(OUT/'source-inventory.json').write_text(json.dumps({'scope':'Current frontend source evidence, not runtime coverage','sources':sources,'controlRows':len(rows),'inputRows':len(inputs),'unmapped':unmapped},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(f'Mapped {len(sources)} sources; {len(rows)} control/caption/tooltip rows; {len(inputs)} input evidence rows.')
if unmapped:raise SystemExit('Unmapped UI sources: '+', '.join(unmapped))
