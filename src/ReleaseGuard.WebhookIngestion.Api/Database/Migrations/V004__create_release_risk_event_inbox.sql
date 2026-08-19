CREATE TABLE release_risk_event_inbox (
    event_id uuid PRIMARY KEY,
    message_key uuid NOT NULL,
    topic text NOT NULL,
    kafka_partition integer NOT NULL,
    kafka_offset bigint NOT NULL,
    event_type text NOT NULL,
    schema_version integer NOT NULL,
    source_provider text NOT NULL,
    event_kind text NOT NULL,
    payload bytea NOT NULL,
    envelope jsonb NOT NULL,
    accepted_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
    CONSTRAINT release_risk_inbox_message_key_matches_event
        CHECK (message_key = event_id),
    CONSTRAINT release_risk_inbox_topic_is_valid
        CHECK (
            topic NOT IN ('', '.', '..')
            AND octet_length(topic) <= 249
            AND topic ~ '^[A-Za-z0-9._-]+$'),
    CONSTRAINT release_risk_inbox_position_is_valid
        CHECK (kafka_partition >= 0 AND kafka_offset >= 0),
    CONSTRAINT release_risk_inbox_event_type_is_valid
        CHECK (event_type = 'releaseguard.release-risk-assessed'),
    CONSTRAINT release_risk_inbox_schema_version_is_valid
        CHECK (schema_version = 1),
    CONSTRAINT release_risk_inbox_source_provider_is_valid
        CHECK (source_provider = 'github'),
    CONSTRAINT release_risk_inbox_event_kind_is_valid
        CHECK (event_kind IN ('change_opened', 'change_updated')),
    CONSTRAINT release_risk_inbox_payload_is_not_empty
        CHECK (octet_length(payload) > 0),
    CONSTRAINT release_risk_inbox_envelope_matches_columns
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
    CONSTRAINT release_risk_inbox_kafka_position_key
        UNIQUE (topic, kafka_partition, kafka_offset)
);
