from pydantic import BaseModel, JsonValue


class ProcessingRequest(BaseModel):
    spaceWebHook: str
    chromaWebhook: str
