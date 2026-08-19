ALTER TABLE release_risk_outbox_messages
ADD COLUMN published_at timestamptz,
ADD COLUMN attempt_count integer NOT NULL DEFAULT 0,
ADD COLUMN next_attempt_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
ADD COLUMN claimed_by text,
ADD COLUMN claim_expires_at timestamptz,
ADD CONSTRAINT release_risk_outbox_attempt_count_is_valid
    CHECK (attempt_count >= 0),
ADD CONSTRAINT release_risk_outbox_claim_pair_is_valid
    CHECK ((claimed_by IS NULL) = (claim_expires_at IS NULL)),
ADD CONSTRAINT release_risk_outbox_claim_owner_is_valid
    CHECK (claimed_by IS NULL OR length(claimed_by) BETWEEN 1 AND 128),
ADD CONSTRAINT release_risk_outbox_published_is_not_claimed
    CHECK (
        published_at IS NULL
        OR (claimed_by IS NULL AND claim_expires_at IS NULL)),
ADD CONSTRAINT release_risk_outbox_publish_time_is_valid
    CHECK (published_at IS NULL OR published_at >= created_at);

CREATE INDEX release_risk_outbox_pending_dispatch_idx
ON release_risk_outbox_messages (next_attempt_at, created_at, event_id)
WHERE published_at IS NULL;
