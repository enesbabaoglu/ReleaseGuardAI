from .app import create_app
from .settings import AiExplanationSettings

app = create_app(AiExplanationSettings.from_environment())
