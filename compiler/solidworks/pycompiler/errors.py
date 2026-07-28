"""Compiler error types.

Two kinds, matching the validation split (ADR-004 / master-architecture):
  - IRValidationError: plan-time STRUCTURAL failure (bad type, missing param, unresolved node
    reference, unsupported v0-exp selector). Raised BEFORE any execution call.
  - FeatureError: compile/exec-time failure mapped to a FEATURE-LEVEL error (a semantic reference
    that won't resolve against live geometry, or a low-level tool that returned FAILED), so the
    failure isn't lost below the IR. Carries node context for the partial-progress report.
"""


class IRValidationError(Exception):
    code = "VALIDATION_FAILED"

    def __init__(self, errors):
        self.errors = list(errors)
        super(IRValidationError, self).__init__("; ".join(self.errors))


class FeatureError(Exception):
    """A per-node failure (reference unresolved / low-level FAILED). code is a machine token."""

    def __init__(self, message, code="FEATURE_FAILED", node_id=None, node_type=None, step=None):
        super(FeatureError, self).__init__(message)
        self.message = message
        self.code = code
        self.node_id = node_id
        self.node_type = node_type
        self.step = step
