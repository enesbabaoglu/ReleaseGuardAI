ALTER TABLE release_risk_event_inbox
    ADD COLUMN explanation_attempt_count integer NOT NULL DEFAULT 0,
    ADD COLUMN explanation_next_attempt_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    ADD COLUMN explanation_claimed_by text,
    ADD COLUMN explanation_claim_expires_at timestamptz,
    ADD COLUMN explanation_completed_at timestamptz,
    ADD COLUMN explanation jsonb,
    ADD CONSTRAINT release_risk_inbox_explanation_attempt_count_is_valid
        CHECK (explanation_attempt_count >= 0),
    ADD CONSTRAINT release_risk_inbox_explanation_claim_is_consistent
        CHECK ((
            explanation_claimed_by IS NULL
            AND explanation_claim_expires_at IS NULL
        ) OR (
            explanation_claimed_by IS NOT NULL
            AND length(btrim(explanation_claimed_by)) > 0
            AND octet_length(explanation_claimed_by) <= 128
            AND explanation_claim_expires_at IS NOT NULL
        )),
    ADD CONSTRAINT release_risk_inbox_explanation_completion_is_consistent
        CHECK ((
            explanation_completed_at IS NULL
            AND explanation IS NULL
        ) OR (
            explanation_completed_at IS NOT NULL
            AND explanation_completed_at >= accepted_at
            AND explanation_claimed_by IS NULL
            AND explanation_claim_expires_at IS NULL
            AND jsonb_typeof(explanation) = 'object'
            AND explanation - ARRAY[
                'eventId',
                'summary',
                'recommendations']::text[] = '{}'::jsonb
            AND explanation ->> 'eventId' = event_id::text
            AND length(btrim(explanation ->> 'summary')) > 0
            AND jsonb_typeof(explanation -> 'recommendations') = 'array'
            AND jsonb_array_length(explanation -> 'recommendations') > 0
            AND NOT jsonb_path_exists(
                explanation,
                '$.recommendations[*] ? (@.type() != "string")')
            AND NOT jsonb_path_exists(
                explanation,
                '$.recommendations[*] ? (@ like_regex "^[[:space:]]*$")')
        ) IS TRUE);

CREATE INDEX release_risk_inbox_explanation_pending_idx
    ON release_risk_event_inbox (
        explanation_next_attempt_at,
        accepted_at,
        event_id)
    WHERE explanation_completed_at IS NULL;
