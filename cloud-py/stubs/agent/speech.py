"""No voice in the cloud: the headset speaks the pet.say events it polls (cloud-py/app.py /events)."""


async def note(pet_id: str, text: str) -> bool:
    return False


async def speak(pet_id: str, text: str) -> bool:
    return False
