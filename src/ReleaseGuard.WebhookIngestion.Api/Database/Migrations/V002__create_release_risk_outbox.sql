ALTER TABLE github_webhook_deliveries
ADD CONSTRAINT github_deliveries_id_disposition_key
    UNIQUE (delivery_id, disposition);

CREATE TABLE release_risk_outbox_messages (
    event_id uuid PRIMARY KEY,
    delivery_disposition text NOT NULL DEFAULT 'accepted',
    event_type text NOT NULL,
    schema_version integer NOT NULL,
    source_provider text NOT NULL,
    event_kind text NOT NULL,
    envelope jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT release_risk_outbox_accepted_delivery_only
        CHECK (delivery_disposition = 'accepted'),
    CONSTRAINT release_risk_outbox_event_type_is_valid
        CHECK (event_type = 'releaseguard.release-risk-assessed'),
    CONSTRAINT release_risk_outbox_schema_version_is_valid
        CHECK (schema_version = 1),
    CONSTRAINT release_risk_outbox_source_provider_is_valid
        CHECK (source_provider = 'github'),
    CONSTRAINT release_risk_outbox_event_kind_is_valid
        CHECK (event_kind IN ('change_opened', 'change_updated')),
    CONSTRAINT release_risk_outbox_envelope_matches_columns
        CHECK ((
            jsonb_typeof(envelope) = 'object'
            AND envelope - ARRAY[
                'eventId',
                'eventType',
                'schemaVersion',
                'sourceProvider',
                'kind',
                'riskInput',
                'riskAssessment']::text[] = '{}'::jsonb
            AND envelope ->> 'eventId' = event_id::text
            AND envelope ->> 'eventType' = event_type
            AND envelope -> 'schemaVersion' = to_jsonb(schema_version)
            AND envelope ->> 'sourceProvider' = source_provider
            AND envelope ->> 'kind' = event_kind
            AND jsonb_typeof(envelope -> 'riskInput') = 'object'
            AND envelope -> 'riskInput' ->> 'sourceDeliveryId' = event_id::text
            AND envelope -> 'riskInput' ->> 'sourceProvider' = source_provider
            AND envelope -> 'riskInput' ->> 'kind' = event_kind
            AND jsonb_typeof(envelope -> 'riskAssessment') = 'object'
        ) IS TRUE),
    CONSTRAINT release_risk_outbox_delivery_fk
        FOREIGN KEY (event_id, delivery_disposition)
        REFERENCES github_webhook_deliveries (delivery_id, disposition)
        ON DELETE RESTRICT
);
