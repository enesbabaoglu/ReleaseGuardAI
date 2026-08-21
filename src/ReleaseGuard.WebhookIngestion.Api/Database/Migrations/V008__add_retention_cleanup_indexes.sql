CREATE INDEX release_risk_outbox_published_retention_idx
    ON release_risk_outbox_messages (published_at, event_id)
    WHERE published_at IS NOT NULL;

CREATE INDEX github_webhook_deliveries_ignored_retention_idx
    ON github_webhook_deliveries (accepted_at, delivery_id)
    WHERE disposition = 'ignored';

CREATE INDEX github_webhook_deliveries_accepted_retention_idx
    ON github_webhook_deliveries (accepted_at, delivery_id)
    WHERE disposition = 'accepted';
