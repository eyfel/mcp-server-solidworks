using SolidworksExecution.Models;

namespace SolidworksExecution.Infrastructure
{
    public interface IOperationGuard
    {
        bool IsDuplicate(string operationId);
        ExecutionResponse GetDuplicate(string operationId);
        bool IsStateVersionValid(int incomingStateVersion);
        void RegisterCompleted(string operationId, ExecutionResponse response);
        int GetCurrentStateVersion();
    }
}
