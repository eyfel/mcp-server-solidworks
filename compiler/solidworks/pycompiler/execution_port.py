"""ExecutionPort — the ONLY way pycompiler reaches the execution layer.

This is the separability boundary (IR-ADR-003): the compiler depends on this small interface,
never on the adapter, FastMCP, or httpx. A concrete implementation lives OUTSIDE pycompiler
(today: adapters/claude/ir_execution_port.py, which delegates to the existing execution_client
over plain REST). Moving the compiler into a standalone REST service later is just a different
ExecutionPort behind the same calls — the lowering / resolver / validation logic is untouched.
"""


class ExecutionPort(object):
    """Port to the deterministic execution layer.

    Contract:
      execute(tool, params, state_version) -> ExecutionResponse dict, exactly as the C# execution
        layer returns it (camelCase): {status, stateVersion, cadState:{features:[...]}, error:{code,
        message}, ...}. status is COMPLETED | FAILED | DUPLICATE. The implementation owns transport
        robustness (e.g. one resync+retry on INVALID_STATE_VERSION) and MUST return the final
        response so the caller can read the resulting stateVersion and thread it to the next op.

      get_state() -> int: the authoritative current state_version (GET /state — read-only, no
        check, no increment). Used to seed a run and to resync afterwards.
    """

    def execute(self, tool, params, state_version):
        raise NotImplementedError

    def get_state(self):
        raise NotImplementedError
