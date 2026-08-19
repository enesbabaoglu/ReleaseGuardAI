ALTER TABLE release_risk_event_inbox
    ADD COLUMN explanation_failed_at timestamptz,
    ADD COLUMN explanation_failure_code text,
    ADD COLUMN explanation_failure_reason text,
    ADD CONSTRAINT release_risk_inbox_explanation_failure_is_consistent
        CHECK ((
            explanation_failed_at IS NULL
            AND explanation_failure_code IS NULL
            AND explanation_failure_reason IS NULL
        ) OR (
            explanation_failed_at IS NOT NULL
            AND explanation_failed_at >= accepted_at
            AND explanation_failure_code IS NOT NULL
            AND explanation_failure_code ~ '^[a-z][a-z0-9_]{0,63}$'
            AND explanation_failure_reason IS NOT NULL
            AND length(btrim(explanation_failure_reason)) > 0
            AND octet_length(explanation_failure_reason) <= 1024
            AND explanation_completed_at IS NULL
            AND explanation IS NULL
            AND explanation_claimed_by IS NULL
            AND explanation_claim_expires_at IS NULL
        )),
    ADD CONSTRAINT release_risk_inbox_explanation_outcome_is_exclusive
        CHECK (NOT (
            explanation_completed_at IS NOT NULL
            AND explanation_failed_at IS NOT NULL
        ));

DROP INDEX release_risk_inbox_explanation_pending_idx;

CREATE INDEX release_risk_inbox_explanation_pending_idx
    ON release_risk_event_inbox (
        explanation_next_attempt_at,
        accepted_at,
        event_id)
    WHERE explanation_completed_at IS NULL
      AND explanation_failed_at IS NULL;

CREATE VIEW release_risk_ai_explanation_failed_work AS
SELECT DISTINCT
    event_id,
    explanation_attempt_count AS attempt_count,
    explanation_failed_at AS failed_at,
    explanation_failure_code AS failure_code,
    explanation_failure_reason AS failure_reason,
    accepted_at,
    envelope
FROM release_risk_event_inbox
WHERE explanation_failed_at IS NOT NULL;
