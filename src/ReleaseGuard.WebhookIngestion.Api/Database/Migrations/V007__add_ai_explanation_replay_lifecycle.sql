CREATE TABLE release_risk_ai_explanation_replays (
    replay_id uuid PRIMARY KEY,
    event_id uuid NOT NULL,
    generation integer NOT NULL,
    requested_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    prior_failed_at timestamptz NOT NULL,
    prior_failure_code text NOT NULL,
    prior_failure_reason text NOT NULL,
    envelope jsonb NOT NULL,
    attempt_count integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    claimed_by text,
    claim_expires_at timestamptz,
    completed_at timestamptz,
    explanation jsonb,
    failed_at timestamptz,
    failure_code text,
    failure_reason text,
    CONSTRAINT release_risk_ai_replay_event_fk
        FOREIGN KEY (event_id)
        REFERENCES release_risk_event_inbox (event_id)
        ON DELETE RESTRICT,
    CONSTRAINT release_risk_ai_replay_generation_is_valid
        CHECK (generation >= 1),
    CONSTRAINT release_risk_ai_replay_event_generation_key
        UNIQUE (event_id, generation),
    CONSTRAINT release_risk_ai_replay_prior_failure_is_valid
        CHECK (
            prior_failure_code ~ '^[a-z][a-z0-9_]{0,63}$'
            AND length(btrim(prior_failure_reason)) > 0
            AND octet_length(prior_failure_reason) <= 1024
            AND prior_failed_at <= requested_at),
    CONSTRAINT release_risk_ai_replay_attempt_count_is_valid
        CHECK (attempt_count >= 0),
    CONSTRAINT release_risk_ai_replay_claim_is_consistent
        CHECK ((
            claimed_by IS NULL
            AND claim_expires_at IS NULL
        ) OR (
            claimed_by IS NOT NULL
            AND length(btrim(claimed_by)) > 0
            AND octet_length(claimed_by) <= 128
            AND claim_expires_at IS NOT NULL
        )),
    CONSTRAINT release_risk_ai_replay_completion_is_consistent
        CHECK ((
            completed_at IS NULL
            AND explanation IS NULL
        ) OR (
            completed_at IS NOT NULL
            AND completed_at >= requested_at
            AND claimed_by IS NULL
            AND claim_expires_at IS NULL
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
        ) IS TRUE),
    CONSTRAINT release_risk_ai_replay_failure_is_consistent
        CHECK ((
            failed_at IS NULL
            AND failure_code IS NULL
            AND failure_reason IS NULL
        ) OR (
            failed_at IS NOT NULL
            AND failed_at >= requested_at
            AND failure_code IS NOT NULL
            AND failure_code ~ '^[a-z][a-z0-9_]{0,63}$'
            AND failure_reason IS NOT NULL
            AND length(btrim(failure_reason)) > 0
            AND octet_length(failure_reason) <= 1024
            AND completed_at IS NULL
            AND explanation IS NULL
            AND claimed_by IS NULL
            AND claim_expires_at IS NULL
        )),
    CONSTRAINT release_risk_ai_replay_outcome_is_exclusive
        CHECK (NOT (completed_at IS NOT NULL AND failed_at IS NOT NULL)),
    CONSTRAINT release_risk_ai_replay_envelope_matches_event
        CHECK ((
            jsonb_typeof(envelope) = 'object'
            AND envelope ->> 'eventId' = event_id::text
            AND envelope -> 'schemaVersion' = '1'::jsonb
            AND envelope ->> 'eventType' = 'releaseguard.release-risk-assessed'
        ) IS TRUE)
);

CREATE UNIQUE INDEX release_risk_ai_replay_one_pending_per_event_idx
    ON release_risk_ai_explanation_replays (event_id)
    WHERE completed_at IS NULL AND failed_at IS NULL;

CREATE INDEX release_risk_ai_replay_pending_idx
    ON release_risk_ai_explanation_replays (
        next_attempt_at,
        requested_at,
        replay_id)
    WHERE completed_at IS NULL AND failed_at IS NULL;

CREATE INDEX release_risk_ai_replay_latest_idx
    ON release_risk_ai_explanation_replays (event_id, generation DESC);

CREATE VIEW release_risk_ai_explanation_replay_history AS
SELECT DISTINCT
    replay_id,
    event_id,
    generation,
    requested_at,
    prior_failed_at,
    prior_failure_code,
    prior_failure_reason,
    attempt_count,
    completed_at,
    failed_at,
    failure_code,
    failure_reason
FROM release_risk_ai_explanation_replays;
