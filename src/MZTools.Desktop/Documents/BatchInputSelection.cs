namespace MZTools.Desktop.Documents;

internal static class BatchInputSelection
{
    internal static string[] Scan(string path,bool recursive,string filter,BatchOperation operation,string target,CancellationToken ct) =>
        BatchProcessService.Scan(path,recursive,filter,ct).Where(p=>BatchMediaPolicy.Supports(operation,p,target)).ToArray();
}
