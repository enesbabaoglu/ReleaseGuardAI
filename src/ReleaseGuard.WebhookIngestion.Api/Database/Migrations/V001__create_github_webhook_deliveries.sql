CREATE TABLE github_webhook_deliveries (
    delivery_id uuid PRIMARY KEY,
    event_name text NOT NULL,
    payload jsonb NOT NULL,
    disposition text NOT NULL,
    risk_input jsonb NULL,
    risk_assessment jsonb NULL,
    accepted_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT github_webhook_deliveries_payload_is_object
        CHECK (jsonb_typeof(payload) = 'object'),
    CONSTRAINT github_webhook_deliveries_disposition_is_valid
        CHECK (disposition IN ('accepted', 'ignored')),
    CONSTRAINT github_webhook_deliveries_risk_shape_matches_disposition
        CHECK (
            (disposition = 'accepted' AND risk_input IS NOT NULL AND risk_assessment IS NOT NULL)
            OR
            (disposition = 'ignored' AND risk_input IS NULL AND risk_assessment IS NULL)
        )
);
