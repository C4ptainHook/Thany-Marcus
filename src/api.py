from fastapi import FastAPI
from models import ProcessingRequest

app = FastAPI()


@app.post("/v1/process_batch", status_code=202)
async def process_batch(request: ProcessingRequest):
    pass


@app.get("/v1/status/{batch_id}")
def get_status(batch_id: str):
    pass


@app.post("/v1/cancel/{batch_id}")
def cancel_batch(batch_id: str):
    pass
