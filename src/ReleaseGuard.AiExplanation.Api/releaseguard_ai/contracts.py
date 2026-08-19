from __future__ import annotations

from typing import Annotated, Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, model_validator

NonEmptyString = Annotated[str, Field(strict=True, min_length=1)]
NonNegativeInt = Annotated[int, Field(strict=True, ge=0)]
RiskScore = Annotated[int, Field(strict=True, ge=0, le=100)]
FactorPoints = Annotated[int, Field(strict=True, ge=0, le=100)]


class StrictContractModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class ReleaseRiskInput(StrictContractModel):
    sourceDeliveryId: UUID
    sourceProvider: Literal["github"]
    kind: Literal["change_opened", "change_updated"]
    repository: NonEmptyString
    changeNumber: NonNegativeInt
    title: NonEmptyString
    author: NonEmptyString
    baseBranch: NonEmptyString
    headBranch: NonEmptyString
    isDraft: bool = Field(strict=True)
    changedFiles: NonNegativeInt
    additions: NonNegativeInt
    deletions: NonNegativeInt


class ReleaseRiskFactor(StrictContractModel):
    code: NonEmptyString
    points: FactorPoints
    reason: NonEmptyString


class ReleaseRiskAssessment(StrictContractModel):
    score: RiskScore
    level: Literal["low", "medium", "high"]
    factors: list[ReleaseRiskFactor]


class ReleaseRiskAssessedV1(StrictContractModel):
    eventId: UUID
    eventType: Literal["releaseguard.release-risk-assessed"]
    schemaVersion: Literal[1]
    sourceProvider: Literal["github"]
    kind: Literal["change_opened", "change_updated"]
    riskInput: ReleaseRiskInput
    riskAssessment: ReleaseRiskAssessment

    @model_validator(mode="after")
    def validate_snapshot_identity(self) -> ReleaseRiskAssessedV1:
        if self.eventId != self.riskInput.sourceDeliveryId:
            raise ValueError("eventId must match riskInput.sourceDeliveryId")
        if self.sourceProvider != self.riskInput.sourceProvider:
            raise ValueError("sourceProvider must match riskInput.sourceProvider")
        if self.kind != self.riskInput.kind:
            raise ValueError("kind must match riskInput.kind")
        return self


class RiskExplanationContent(StrictContractModel):
    summary: NonEmptyString
    recommendations: Annotated[
        list[NonEmptyString],
        Field(min_length=1),
    ]


class ReleaseRiskExplanationV1(StrictContractModel):
    eventId: UUID
    summary: NonEmptyString
    recommendations: Annotated[
        list[NonEmptyString],
        Field(min_length=1),
    ]
