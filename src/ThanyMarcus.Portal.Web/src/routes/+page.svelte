<script lang="ts">
  export let data: {
    me: {
      userId: string;
      email: string;
      name: string;
      profilePictureUrl: string | null;
      totp: string;
    } | null;
  };
</script>

<main>
  <h1>Thany-Marcus Portal</h1>
  {#if data.me}
    <p>Signed in as <strong>{data.me.name}</strong> ({data.me.email})</p>
    {#if data.me.profilePictureUrl}
      <img src={data.me.profilePictureUrl} alt="" width="64" height="64" />
    {/if}
    <p>TOTP state: {data.me.totp}</p>
    <form method="post" action="/api/auth/signout">
      <button type="submit">Sign out</button>
    </form>
  {:else}
    <p><a href="/api/auth/signin">Sign in with Google</a></p>
  {/if}
</main>

<style>
  main {
    font-family: system-ui, -apple-system, sans-serif;
    max-width: 40rem;
    margin: 4rem auto;
    padding: 0 1rem;
    color: #1a1a1a;
  }
  h1 {
    font-size: 2rem;
    margin-bottom: 0.5rem;
  }
  p {
    color: #555;
  }
</style>
